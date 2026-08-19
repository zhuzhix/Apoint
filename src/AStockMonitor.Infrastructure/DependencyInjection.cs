using AStockMonitor.Infrastructure.Configuration;
using AStockMonitor.Infrastructure.Analytics;
using AStockMonitor.Infrastructure.Persistence;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Application.Market;
using AStockMonitor.Infrastructure.Market;
using AStockMonitor.Application.Recovery;
using AStockMonitor.Infrastructure.Recovery;
using AStockMonitor.Application.Strategies;
using AStockMonitor.Infrastructure.Strategies;
using AStockMonitor.Application.Collection;
using AStockMonitor.Infrastructure.Collection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AStockMonitor.Infrastructure;

/// <summary>注册行情持久化和对子趋势回测所需的基础设施服务。</summary>
public static class DependencyInjection
{
    /// <summary>从 Market 配置节创建数据库、Redis 和回测服务。</summary>
    public static IServiceCollection AddAStockInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection("Market").Get<MarketOptions>() ?? new MarketOptions();
        var recoveryOptions = configuration.GetSection("MarketRecovery").Get<MarketRecoveryOptions>()
            ?? new MarketRecoveryOptions();
        var previewOptions = configuration.GetSection("IntradayPreview").Get<IntradayPreviewOptions>()
            ?? new IntradayPreviewOptions();
        var collectionV4Options = configuration.GetSection("MarketCollectionV4")
            .Get<MarketCollectionV4Options>() ?? new MarketCollectionV4Options();
        var collectorControlOptions = configuration.GetSection("CollectorControl")
            .Get<CollectorControlOptions>() ?? new CollectorControlOptions();
        var collectorOperationsOptions = configuration.GetSection("CollectorOperations")
            .Get<CollectorOperationsOptions>() ?? new CollectorOperationsOptions();
        var authoritativeUniverseOptions = configuration.GetSection("AuthoritativeUniverse")
            .Get<AuthoritativeUniverseOptions>() ?? new AuthoritativeUniverseOptions();
        var pairTrendQueryOptions = configuration.GetSection("PairTrendQuery")
            .Get<PairTrendQueryOptions>() ?? new PairTrendQueryOptions();
        pairTrendQueryOptions.Validate();

        services.AddSingleton(options);
        services.AddSingleton(recoveryOptions);
        services.AddSingleton(previewOptions);
        services.AddSingleton(collectionV4Options);
        services.AddSingleton(collectorControlOptions);
        services.AddSingleton(collectorOperationsOptions);
        services.AddSingleton(authoritativeUniverseOptions);
        services.AddSingleton(pairTrendQueryOptions);
        services.AddSingleton<IMySqlConnectionFactory>(_ => new MySqlConnectionFactory(options.MySqlConnection));
        services.AddSingleton<ITradingDayGate, MySqlTradingDayGate>();
        services.AddSingleton<RedisConnectionProvider>();
        services.AddSingleton<IReliableTickPublisher, RedisTickStreamPublisher>();
        services.AddSingleton<RedisIntradayPreviewStore>();
        services.AddSingleton<IIntradayPreviewStore>(provider =>
            provider.GetRequiredService<RedisIntradayPreviewStore>());
        services.AddSingleton<IOfficialBarReader, MySqlOfficialBarReader>();
        services.AddSingleton<ICanonicalBarWriter, CanonicalBarWriter>();
        services.AddSingleton<IOfficialBarBatchWriter, OfficialBarBatchWriter>();
        services.AddSingleton<IMarketRecoveryRepository, MySqlMarketRecoveryRepository>();
        services.AddSingleton<ICollectorCommandRepository, MySqlCollectorCommandRepository>();
        services.AddSingleton<ICollectorOperationsRepository, MySqlCollectorOperationsRepository>();
        services.AddSingleton<IAuthoritativeUniverseRepository, MySqlAuthoritativeUniverseRepository>();
        services.AddSingleton<IMarketGapDetectionService, MarketGapDetectionService>();
        if (pairTrendQueryOptions.HistoricalReplayEnabled)
            services.AddSingleton<IPairTrendBacktestService, PairTrendBacktestService>();
        services.AddSingleton<IPairTrendLiveSnapshotWriter, PairTrendLiveSnapshotWriter>();
        services.AddSingleton<IPairTrendActiveLevelCache, PairTrendActiveLevelCache>();
        services.AddSingleton<IPairTrendRealtimeService, PairTrendRealtimeService>();
        services.AddSingleton<IPairTrendTickInvalidationService, PairTrendTickInvalidationService>();
        services.AddSingleton<IStrategyMarketDataReader, StrategyMarketDataReader>();
        services.AddSingleton<IStrategyRepository, MySqlStrategyRepository>();
        services.AddSingleton<IStrategyReplayTaskRepository, MySqlStrategyReplayTaskRepository>();
        return services;
    }

    /// <summary>注册需要进程内行情状态的分层查询服务；仅由 API 主机调用。</summary>
    public static IServiceCollection AddMarketDataReadServices(this IServiceCollection services)
    {
        services.AddSingleton<IMarketDataReader, LayeredMarketDataReader>();
        return services;
    }
}
