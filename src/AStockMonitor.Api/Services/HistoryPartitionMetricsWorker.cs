using AStockMonitor.Infrastructure.Observability;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Api.Services;

/// <summary>
/// Reads one low-cardinality MySQL aggregate every ten seconds and publishes it through OTLP.
/// Monitoring failures are isolated from the history downloader and API request pipeline.
/// </summary>
public sealed class HistoryPartitionMetricsWorker(
    IMySqlConnectionFactory connectionFactory,
    ILogger<HistoryPartitionMetricsWorker> logger) : BackgroundService
{
    private long? _lastAttemptId;
    private long? _lastRowsWritten;
    private long? _lastCompletePartitions;
    private DateTimeOffset? _lastSampleAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AStockObservability.ComponentStarted("history-partition-monitor");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AStockObservability.RecordHistoryMetricsPollFailure();
                logger.LogWarning(exception, "历史K线分区指标采集失败；下载调度不受影响");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            SELECT id AS BatchId, rows_written AS RowsWritten
            FROM bar_ingest_batch ORDER BY id DESC LIMIT 1;

            SELECT p.status AS Status, COUNT(*) AS PartitionCount,
                   COALESCE(SUM(CASE WHEN p.status IN ('pending','retry_waiting')
                                    THEN p.symbol_count ELSE 0 END),0) AS PendingSymbols,
                   COALESCE(SUM(p.status='running'),0) AS ActiveWorkers,
                   COALESCE(MAX(CASE WHEN p.status='running'
                       THEN TIMESTAMPDIFF(SECOND,p.heartbeat_at,UTC_TIMESTAMP(6)) ELSE 0 END),0)
                       AS MaxHeartbeatAgeSeconds,
                   COALESCE(MAX(CASE WHEN p.status='running'
                       THEN TIMESTAMPDIFF(SECOND,p.progress_at,UTC_TIMESTAMP(6)) ELSE 0 END),0)
                       AS MaxProgressAgeSeconds
            FROM bar_ingest_partition p
            WHERE p.batch_id=(SELECT id FROM bar_ingest_batch ORDER BY id DESC LIMIT 1)
            GROUP BY p.status;

            SELECT COALESCE(MAX(id),0)
            FROM bar_ingest_partition_attempt;

            SELECT EXISTS(
                SELECT 1 FROM history_scheduler_lease
                WHERE lease_name='official-kline-history-scheduler'
                  AND lease_expires_at>UTC_TIMESTAMP(6));
            """, commandTimeout: 3, cancellationToken: cancellationToken));

        var batch = await grid.ReadSingleOrDefaultAsync<MetricBatch>();
        var rows = (await grid.ReadAsync<MetricStatusRow>()).ToArray();
        var maxAttemptId = await grid.ReadSingleAsync<long>();
        var leaseValid = await grid.ReadSingleAsync<bool>();

        if (_lastAttemptId is long previousAttemptId && maxAttemptId > previousAttemptId)
        {
            var failures = await connection.QueryAsync<MetricFailureRow>(new CommandDefinition(
                """
                SELECT failure_code AS FailureCode,COUNT(*) AS FailureCount
                FROM bar_ingest_partition_attempt
                WHERE id>@AfterId AND id<=@UntilId
                  AND failure_code IN ('HEARTBEAT_LOST','NO_PROGRESS','PROCESS_EXIT')
                GROUP BY failure_code;
                """, new { AfterId = previousAttemptId, UntilId = maxAttemptId },
                commandTimeout: 3, cancellationToken: cancellationToken));
            foreach (var failure in failures)
            {
                AStockObservability.RecordHistoryWatchdogTermination(
                    failure.FailureCode.ToLowerInvariant(), failure.FailureCount);
            }
        }
        _lastAttemptId = maxAttemptId;

        var byStatus = rows.ToDictionary(item => item.Status, item => item.PartitionCount);
        var totalPartitions = byStatus.Values.Sum();
        var completedPartitions = byStatus.GetValueOrDefault("complete");
        var pendingSymbols = Convert.ToInt64(rows.Sum(item => item.PendingSymbols));
        var activeWorkers = Convert.ToInt64(
            rows.Select(item => item.ActiveWorkers).DefaultIfEmpty(0).Max());
        var heartbeatAge = rows.Select(item => item.MaxHeartbeatAgeSeconds).DefaultIfEmpty(0).Max();
        var progressAge = rows.Select(item => item.MaxProgressAgeSeconds).DefaultIfEmpty(0).Max();
        var now = DateTimeOffset.UtcNow;
        var elapsedSeconds = Math.Max(0.001, (now - (_lastSampleAt ?? now)).TotalSeconds);
        var rowsRate = _lastRowsWritten is null || batch is null
            ? 0
            : Math.Max(0, batch.RowsWritten - _lastRowsWritten.Value) / elapsedSeconds;
        var completedRate = _lastCompletePartitions is null
            ? 0
            : Math.Max(0, completedPartitions - _lastCompletePartitions.Value) / elapsedSeconds;
        var remainingPartitions = Math.Max(0, totalPartitions - completedPartitions);
        var eta = completedRate > 0 ? remainingPartitions / completedRate : 0;

        AStockObservability.UpdateHistoryPartitionSnapshot(new HistoryPartitionMetricSnapshot(
            byStatus,
            activeWorkers,
            pendingSymbols,
            totalPartitions == 0 ? 0 : (double)completedPartitions / totalPartitions,
            batch?.RowsWritten ?? 0,
            rowsRate,
            heartbeatAge,
            progressAge,
            eta,
            leaseValid));

        _lastRowsWritten = batch?.RowsWritten;
        _lastCompletePartitions = completedPartitions;
        _lastSampleAt = now;
    }

    private sealed class MetricBatch
    {
        public long BatchId { get; init; }
        public long RowsWritten { get; init; }
    }

    private sealed class MetricStatusRow
    {
        public string Status { get; init; } = "";
        public long PartitionCount { get; init; }
        public decimal PendingSymbols { get; init; }
        public decimal ActiveWorkers { get; init; }
        public long MaxHeartbeatAgeSeconds { get; init; }
        public long MaxProgressAgeSeconds { get; init; }
    }

    private sealed class MetricFailureRow
    {
        public string FailureCode { get; init; } = "";
        public long FailureCount { get; init; }
    }
}
