using AStockMonitor.Application.Collection;
using AStockMonitor.Application.Market;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AStockMonitor.Api.Health;

/// <summary>仅证明 ASP.NET 进程仍能执行代码。</summary>
public sealed class MarketLivenessHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy("API process is alive"));
}

/// <summary>
/// 新采集架构的就绪检查：API 静态站点、MySQL 和交易时段采集心跳。
/// Python 通过 HTTP 推送 K 线，因此这里不再依赖 Redis。
/// </summary>
public sealed class MarketHealthCheck(
    IMySqlConnectionFactory connectionFactory,
    ICollectorOperationsRepository collectorOperations,
    IAuthoritativeUniverseRepository universeRepository,
    CollectorOperationsOptions options,
    IWebHostEnvironment environment) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();
        var failures = new List<string>();
        CollectorOperationsSnapshot? operations = null;

        try
        {
            await using var connection = connectionFactory.Create();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT 1;", cancellationToken: cancellationToken));
            operations = await collectorOperations.GetSnapshotAsync(cancellationToken);
            data["mysql"] = "connected";
            data["activeBlacklistSymbols"] = operations.ActiveBlacklistCount;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add("MySqlOrOperationsSchemaUnavailable:" + exception.GetType().Name);
        }

        var webRoot = environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
            webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");
        var indexExists = File.Exists(Path.Combine(webRoot, "index.html"));
        data["websiteIndex"] = indexExists ? "present" : "missing";
        if (!indexExists) failures.Add("WebsiteIndexMissing");

        var chinaNow = ChinaMarketSession.ToChinaTime(DateTimeOffset.UtcNow);
        var tradingDate = DateOnly.FromDateTime(chinaNow.Date);
        var tradingDay = false;
        try
        {
            var universe = await universeRepository.GetStatusAsync(tradingDate, cancellationToken);
            if (universe is null)
            {
                failures.Add("AuthoritativeUniverseMissing");
                data["authoritativeUniverse"] = "missing";
            }
            else
            {
                tradingDay = universe.IsTradingDay;
                data["authoritativeUniverse"] = universe.IsReady ? "ready" : "inconsistent";
                data["authoritativeUniverseDate"] = universe.TradingDate.ToString("yyyy-MM-dd");
                data["authoritativeUniverseSymbols"] = universe.TotalSymbols;
                if (!universe.IsReady) failures.Add("AuthoritativeUniverseInconsistent");
            }
            data["isTradingDay"] = tradingDay;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add("TradingCalendarUnavailable:" + exception.GetType().Name);
        }

        if (operations is not null)
        {
            var newest = operations.Collectors.FirstOrDefault();
            var heartbeatAge = newest is null
                ? long.MaxValue
                : Math.Max(0, (long)(operations.DatabaseUtcNow - newest.LastSeenAt).TotalSeconds);
            data["collectorHeartbeatAgeSeconds"] = heartbeatAge == long.MaxValue
                ? "never"
                : heartbeatAge;
            data["collectorProcesses"] = newest?.ProcessesRunning ?? 0;
            if (tradingDay && IsTradingSession(chinaNow.TimeOfDay) &&
                (newest is null || heartbeatAge > Math.Clamp(options.HeartbeatOfflineSeconds, 10, 600)))
                failures.Add("CollectorHeartbeatMissing");
            if (tradingDay && IsTradingSession(chinaNow.TimeOfDay) && newest is not null &&
                (newest.ProcessesExpected != Math.Clamp(options.ExpectedProcesses, 1, 64) ||
                 newest.ProcessesRunning != Math.Clamp(options.ExpectedProcesses, 1, 64) ||
                 newest.Processes.Count != Math.Clamp(options.ExpectedProcesses, 1, 64)))
                failures.Add("CollectorProcessCountMismatch");
        }

        return failures.Count == 0
            ? HealthCheckResult.Healthy("API, website and collector control plane are ready", data)
            : HealthCheckResult.Unhealthy(string.Join(',', failures), data: data);
    }

    private static bool IsTradingSession(TimeSpan time) =>
        time >= new TimeSpan(9, 25, 0) && time <= new TimeSpan(11, 35, 0) ||
        time >= new TimeSpan(12, 55, 0) && time <= new TimeSpan(15, 5, 0);
}
