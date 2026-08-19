using AStockMonitor.Infrastructure.Observability;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Api.Services;

/// <summary>
/// Refreshes exact dataset counts outside the HTTP request path. The status API
/// reads this snapshot in constant time even when K-line tables contain millions of rows.
/// </summary>
public sealed class DatasetStatsWorker(
    IMySqlConnectionFactory connectionFactory,
    ILogger<DatasetStatsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AStockObservability.ComponentStarted("dataset-stats");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RefreshAsync(stoppingToken);
                AStockObservability.RecordPipelineBatch("dataset-stats", "mysql", 9, 0, 0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AStockObservability.RecordFailure("dataset-stats");
                logger.LogWarning(exception, "数据集统计快照刷新失败，状态接口继续返回上一次快照。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dataset_stat_snapshot(dataset_name,row_count,is_exact,updated_at)
            SELECT 'instrument_daily_status',COUNT(*),TRUE,UTC_TIMESTAMP(6) FROM instrument_daily_status
            UNION ALL SELECT 'kline_bar_5m',COUNT(*),TRUE,UTC_TIMESTAMP(6) FROM kline_bar_5m
            UNION ALL SELECT 'kline_bar_30m',COUNT(*),TRUE,UTC_TIMESTAMP(6) FROM kline_bar_agg WHERE frequency='30m'
            UNION ALL SELECT 'kline_bar_60m',COUNT(*),TRUE,UTC_TIMESTAMP(6) FROM kline_bar_agg WHERE frequency='60m'
            UNION ALL SELECT 'kline_bar_daily',COUNT(*),TRUE,UTC_TIMESTAMP(6) FROM kline_bar_daily
            UNION ALL SELECT 'pair_pivot_signal',COUNT(*),TRUE,UTC_TIMESTAMP(6) FROM pair_pivot_signal
            UNION ALL SELECT 'pair_trend_event',COUNT(*),TRUE,UTC_TIMESTAMP(6) FROM pair_trend_event
            UNION ALL SELECT 'pair_trend_hit',COUNT(*),TRUE,UTC_TIMESTAMP(6) FROM pair_trend_hit
            UNION ALL SELECT 'bar_quality_issue_open',COUNT(*),TRUE,UTC_TIMESTAMP(6) FROM bar_quality_issue WHERE status='open'
            ON DUPLICATE KEY UPDATE row_count=VALUES(row_count),is_exact=TRUE,updated_at=VALUES(updated_at);
            """;
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            sql, commandTimeout: 180, cancellationToken: cancellationToken));
    }
}
