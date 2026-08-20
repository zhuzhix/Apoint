using System.Reflection;
using AStockMonitor.Api.Health;
using AStockMonitor.Api.Hubs;
using AStockMonitor.Api.Middleware;
using AStockMonitor.Api.Services;
using AStockMonitor.Application.Market;
using AStockMonitor.Infrastructure;
using AStockMonitor.Infrastructure.Configuration;
using AStockMonitor.Infrastructure.Observability;
using Microsoft.OpenApi;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

var marketOptions = builder.Configuration.GetSection("Market").Get<MarketOptions>()
                    ?? new MarketOptions();
var legacyRedisWorkersEnabled =
    builder.Configuration.GetValue<bool>("LegacyRedisWorkers:Enabled");
MarketConfigurationValidator.Validate(
    marketOptions, MarketHostRole.Api, redisPipelineEnabled: legacyRedisWorkersEnabled);

// Windows 使用 Windows Service；Linux 使用 systemd。两种托管方式共享同一套
// Web/API 代码，避免把部署平台差异扩散到业务层。
if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "AStockMonitor.Api";
    });
}
else
{
    builder.Services.AddSystemd();
}

// Use console logging for both interactive runs and Windows Service hosting.
// This prevents the default EventLog provider from requiring elevated access
// during local development and keeps service logs visible to the configured
// Windows service log sink in production.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddAStockOpenTelemetry(builder.Configuration, "AStockMonitor.Api");

builder.Services.AddControllers();
builder.Services.AddGrpc();
builder.Services.AddSignalR().AddMessagePackProtocol();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "A股监控程序 API",
        Version = "v1",
        Description = "A股实时行情、历史 K 线数据质量和对子趋势顶底回测查询接口。"
    });

    // XML 文档同时为接口、查询参数和响应 DTO 提供中文说明。
    // 文件名取当前程序集，避免发布目录变化后使用固定绝对路径。
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);

    // 泛型分页模型使用 PagedResponseOfXxx，避免 Swagger UI 展示程序集限定名。
    options.CustomSchemaIds(type =>
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var genericName = type.Name[..type.Name.IndexOf('`')];
        var argumentNames = string.Join("And", type.GetGenericArguments().Select(static item => item.Name));
        return $"{genericName}Of{argumentNames}";
    });
});
builder.Services.AddHealthChecks()
    .AddCheck<MarketLivenessHealthCheck>("process", tags: ["live"])
    .AddCheck<MarketHealthCheck>("market-chain", tags: ["ready"]);
builder.Services.AddAStockInfrastructure(builder.Configuration);
builder.Services.AddAStockObservability(
    builder.Configuration, "AStockMonitor.Api", includeAspNetCore: true);

builder.Services.AddSingleton<MarketRuntimeState>();
builder.Services.AddSingleton<CollectorGatewayRequestAuthenticator>();
builder.Services.AddSingleton<PairTrendCollectionSessionStore>();
builder.Services.AddSingleton<PairTrendCollectionPlanProvider>();
builder.Services.AddSingleton<PairTrendCollectionComputeQueue>();
builder.Services.AddSingleton<CollectorOperationsReportService>();
builder.Services.AddSingleton<OperationsStatusService>();
builder.Services.AddSingleton<AuthoritativeUniverseSyncService>();
builder.Services.AddSingleton<WaveBottomCollectionService>();
builder.Services.AddSingleton<PairTrendNextDayValidationService>();
builder.Services.AddSingleton<PairTrendQueryService>();
builder.Services.AddSingleton<PairTrendQueryCache>();
builder.Services.AddMemoryCache(options => options.SizeLimit = 10_000);
builder.Services.AddSingleton<MarketEventBus>();
builder.Services.AddSingleton(new MarketMemoryOptions
{
    RecentTicksPerSymbol = builder.Configuration.GetValue("Market:RecentTicksPerSymbol", 256)
});
builder.Services.AddSingleton<IMarketStateStore, InMemoryMarketStateStore>();
builder.Services.AddMarketDataReadServices();
builder.Services.AddSingleton<MarketEventProcessor>();
builder.Services.AddHostedService<HistoryPartitionMetricsWorker>();
builder.Services.AddHostedService<DatasetStatsWorker>();
builder.Services.AddHostedService<PairTrendCollectionComputeWorker>();

// 新架构不依赖 Redis。仅在兼容旧部署且显式启用时，才启动历史 Redis
// Stream 消费者；默认关闭可确保 Redis 卸载后 API 仍能独立运行。
if (legacyRedisWorkersEnabled)
{
    builder.Services.AddHostedService<QuoteBroadcaster>();
    builder.Services.AddHostedService<BarEventBroadcaster>();
    builder.Services.AddHostedService<PairTrendEventBroadcaster>();
    builder.Services.AddHostedService<StrategySignalBroadcaster>();
    builder.Services.AddHostedService<MarketOperationalMetricsWorker>();
    builder.Services.AddHostedService<NotificationProjectionWorker>();
}

var app = builder.Build();
AStockObservability.ComponentStarted("api");

// 开发环境默认开放 Swagger；生产环境必须显式设置 Swagger:Enabled=true。
// 这样既方便本机联调，也避免部署后无意暴露完整接口结构。
var swaggerEnabled = app.Environment.IsDevelopment() ||
                     app.Configuration.GetValue<bool>("Swagger:Enabled");
if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "A股监控程序 API v1");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "A股监控程序 API";
        options.DisplayRequestDuration();
        options.EnableDeepLinking();
    });
}

// 生产环境由 API 同源托管 Vue SPA；开发环境也可验证生产构建产物。
// UseDefaultFiles 会把根路径内部改写为 index.html，但部分服务器版本不会让
// StaticFileOptions 在原始 "/" 请求上保留 index.html 的缓存头。在响应启动前
// 统一对所有 HTML（包括 Vue fallback）禁用缓存，避免部署后继续引用旧哈希资源。
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.Headers.CacheControl = "no-cache,no-store,must-revalidate";
        }

        return Task.CompletedTask;
    });

    await next();
});
app.UseDefaultFiles();
app.UseMiddleware<PrecompressedStaticFileMiddleware>();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl =
            context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase)
                ? "no-cache,no-store,must-revalidate"
                : context.Context.Request.Path.StartsWithSegments("/assets")
                    ? "public,max-age=31536000,immutable"
                    : "no-cache";
    }
});
app.MapControllers();
if (legacyRedisWorkersEnabled)
    app.MapGrpcService<MarketIngestGrpcService>();
app.MapHub<MarketHub>("/hubs/market");
app.MapHub<StrategyHub>("/hubs/strategy");
app.MapHub<NotificationHub>("/hubs/notifications");
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapGet("/api/status", () => Results.Ok(new
    {
        service = "AStockMonitor.Api",
        version = "0.1.0",
        runtime = ".NET 10",
        status = "ready"
    }))
    .WithName("GetServiceStatus")
    .WithTags("服务状态")
    .WithSummary("查询服务基础状态")
    .WithDescription("返回服务名称、版本、.NET 运行时和基础就绪状态。");

// 未命中 API、Hub、Swagger、健康检查或静态资源时交给 Vue Router。
// 股票代码包含“.”，默认 nonfile 约束会把它当作文件扩展名；显式路由保证
// /stocks/SHSE.600000 直接刷新时仍交给 Vue Router。
app.MapFallbackToFile("/stocks/{**path}", "index.html");
app.MapFallbackToFile("index.html");

app.Run();
