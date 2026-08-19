using AStockMonitor.Application.Market;
using AStockMonitor.Infrastructure.Observability;
using AStockMonitor.Infrastructure.Persistence;
using AStockMonitor.Infrastructure.Market;
using Dapper;
using StackExchange.Redis;

namespace AStockMonitor.Api.Services;

/// <summary>Exports low-cardinality end-to-end market pipeline health every ten seconds.</summary>
public sealed class MarketOperationalMetricsWorker(
    MarketRuntimeState runtimeState,
    IMySqlConnectionFactory connectionFactory,
    RedisConnectionProvider redisProvider,
    ITradingDayGate tradingDayGate,
    ILogger<MarketOperationalMetricsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AStockObservability.ComponentStarted("market-operations-monitor");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try { await PollAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                AStockObservability.RecordFailure("market-operations-monitor");
                logger.LogWarning(exception, "行情链路运维指标采集失败。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var chinaNow = ChinaMarketSession.ToChinaTime(now);
        var isTradingDay = await tradingDayGate.IsTradingDayAsync(
            DateOnly.FromDateTime(chinaNow.Date), cancellationToken);
        var connected = runtimeState.GetCollectors().Where(item =>
            item.Connected && now - item.LastSeenAt < TimeSpan.FromSeconds(30)).ToArray();
        var heartbeatAge = connected.Select(item =>
            (long)Math.Max(0, (now - (item.LastHeartbeatAt ?? item.LastSeenAt)).TotalSeconds))
            .DefaultIfEmpty(0).Max();

        await using var connection = connectionFactory.Create();
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT COUNT(*) FROM market_recovery_run
            WHERE status IN ('validating','planned','running','strategy_recalculating','strategy_running');
            SELECT COUNT(*) FROM market_recovery_item
            WHERE status IN ('planned','running','retry_waiting');
            SELECT COUNT(*) FROM market_recovery_item
            WHERE status IN ('running','retry_waiting')
              AND updated_at<DATE_SUB(UTC_TIMESTAMP(6),INTERVAL 10 MINUTE);
            SELECT COUNT(*) FROM strategy_scan_run
            WHERE status IN ('failed','partial')
              AND started_at>=DATE_SUB(UTC_TIMESTAMP(6),INTERVAL 30 MINUTE);
            SELECT COUNT(*) FROM bar_event_outbox
            WHERE status IN ('pending','retry_waiting','publishing');
            """, commandTimeout: 3, cancellationToken: cancellationToken));

        var redis = (await redisProvider.GetAsync()).GetDatabase();
        var snapshot = ToMap(await redis.HashGetAllAsync("md:v4:snapshot:status"));
        var hotTick = ToMap(await redis.HashGetAllAsync("md:v4:hot-tick:status"));
        var snapshotCompleted = ParseTime(snapshot.GetValueOrDefault("completed_at"));
        AStockObservability.UpdateMarketOperationalSnapshot(new MarketOperationalMetricSnapshot(
            connected.Length, heartbeatAge, connected.Sum(item => item.QueueDepth),
            connected.Sum(item => item.OutboxPendingCount),
            await grid.ReadSingleAsync<long>(), await grid.ReadSingleAsync<long>(),
            await grid.ReadSingleAsync<long>(), await grid.ReadSingleAsync<long>(),
            await grid.ReadSingleAsync<long>(),
            ParseLong(snapshot.GetValueOrDefault("published")),
            ParseLong(snapshot.GetValueOrDefault("stale")),
            ParseDouble(snapshot.GetValueOrDefault("elapsed_ms")),
            snapshotCompleted is null ? long.MaxValue :
                (long)Math.Max(0, (now - snapshotCompleted.Value).TotalSeconds),
            ParseLong(hotTick.GetValueOrDefault("desired_count")),
            ParseLong(hotTick.GetValueOrDefault("worker_count")),
            ParseLong(hotTick.GetValueOrDefault("base_candidate_count")),
            ParseLong(hotTick.GetValueOrDefault("intraday_candidate_count")),
            ParseLong(hotTick.GetValueOrDefault("overflow_count")),
            isTradingDay ? 1 : 0));
    }

    private static Dictionary<string, string> ToMap(HashEntry[] entries) => entries.ToDictionary(
        static entry => entry.Name.ToString(), static entry => entry.Value.ToString(),
        StringComparer.OrdinalIgnoreCase);

    private static long ParseLong(string? value) => long.TryParse(value, out var parsed) ? parsed : 0;
    private static double ParseDouble(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}
