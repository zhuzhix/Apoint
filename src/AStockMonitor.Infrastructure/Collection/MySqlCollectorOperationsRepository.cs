using System.Text.Json;
using AStockMonitor.Application.Collection;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Infrastructure.Collection;

public sealed class MySqlCollectorOperationsRepository(
    IMySqlConnectionFactory connectionFactory) : ICollectorOperationsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RecordHeartbeatAsync(
        CollectorHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO pair_trend_collector_heartbeat(
                collector_id,instance_id,status,processes_expected,processes_running,
                active_jobs,queued_jobs,succeeded_jobs,retrying_jobs,failed_jobs,
                blacklisted_symbols,cycles_completed,current_cycle_id,host_name,
                app_version,started_at,workers_json,last_error,last_error_at,last_seen_at)
            VALUES(
                @CollectorId,@InstanceId,@Status,@ProcessesExpected,@ProcessesRunning,
                @ActiveJobs,@QueuedJobs,@SucceededJobs,@RetryingJobs,@FailedJobs,
                @BlacklistedSymbols,@CyclesCompleted,@CurrentCycleId,@HostName,
                @Version,@StartedAt,@WorkersJson,@LastError,
                CASE WHEN @LastError IS NULL THEN NULL ELSE UTC_TIMESTAMP(6) END,
                UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
                instance_id=VALUES(instance_id),status=VALUES(status),
                processes_expected=VALUES(processes_expected),
                processes_running=VALUES(processes_running),active_jobs=VALUES(active_jobs),
                queued_jobs=VALUES(queued_jobs),succeeded_jobs=VALUES(succeeded_jobs),
                retrying_jobs=VALUES(retrying_jobs),failed_jobs=VALUES(failed_jobs),
                blacklisted_symbols=VALUES(blacklisted_symbols),
                cycles_completed=VALUES(cycles_completed),
                current_cycle_id=VALUES(current_cycle_id),host_name=VALUES(host_name),
                app_version=VALUES(app_version),started_at=VALUES(started_at),
                workers_json=VALUES(workers_json),
                -- A healthy heartbeat is an explicit recovery signal. Keeping the
                -- previous text here makes the operations page report a stale error
                -- forever even after the collector has recovered.
                last_error=VALUES(last_error),
                last_error_at=CASE WHEN VALUES(last_error) IS NULL
                    THEN NULL ELSE UTC_TIMESTAMP(6) END,
                last_seen_at=UTC_TIMESTAMP(6);
            """;

        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            heartbeat.CollectorId,
            heartbeat.InstanceId,
            heartbeat.Status,
            heartbeat.ProcessesExpected,
            heartbeat.ProcessesRunning,
            heartbeat.ActiveJobs,
            heartbeat.QueuedJobs,
            heartbeat.SucceededJobs,
            heartbeat.RetryingJobs,
            heartbeat.FailedJobs,
            heartbeat.BlacklistedSymbols,
            heartbeat.CyclesCompleted,
            heartbeat.CurrentCycleId,
            heartbeat.HostName,
            heartbeat.Version,
            StartedAt = heartbeat.StartedAt,
            WorkersJson = JsonSerializer.Serialize(heartbeat.Processes, JsonOptions),
            heartbeat.LastError
        }, cancellationToken: cancellationToken));
    }

    public async Task<CollectorBlacklistEntry> BlacklistAsync(
        string collectorId,
        string symbol,
        int failureCount,
        string reason,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO pair_trend_symbol_blacklist(
                symbol,collector_id,failure_count,reason,blacklisted_at,expires_at)
            VALUES(@Symbol,@CollectorId,@FailureCount,@Reason,UTC_TIMESTAMP(6),
                DATE_ADD(UTC_TIMESTAMP(6),INTERVAL @DurationSeconds SECOND))
            ON DUPLICATE KEY UPDATE
                collector_id=VALUES(collector_id),
                failure_count=GREATEST(failure_count,VALUES(failure_count)),
                reason=VALUES(reason),blacklisted_at=UTC_TIMESTAMP(6),
                expires_at=VALUES(expires_at);

            SELECT symbol AS Symbol,collector_id AS CollectorId,
                failure_count AS FailureCount,reason AS Reason,
                blacklisted_at AS BlacklistedAt,expires_at AS ExpiresAt
            FROM pair_trend_symbol_blacklist WHERE symbol=@Symbol;
            """;

        await using var connection = connectionFactory.Create();
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(sql, new
        {
            Symbol = symbol,
            CollectorId = collectorId,
            FailureCount = failureCount,
            Reason = reason,
            DurationSeconds = Math.Max(60, (long)duration.TotalSeconds)
        }, cancellationToken: cancellationToken));
        return await grid.ReadSingleAsync<CollectorBlacklistEntry>();
    }

    public async Task<IReadOnlySet<string>> GetActiveBlacklistedSymbolsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT symbol FROM pair_trend_symbol_blacklist
            WHERE expires_at > UTC_TIMESTAMP(6);
            """;
        await using var connection = connectionFactory.Create();
        var symbols = await connection.QueryAsync<string>(new CommandDefinition(
            sql, cancellationToken: cancellationToken));
        return symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<CollectorOperationsSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT UTC_TIMESTAMP(6);

            SELECT collector_id AS CollectorId,instance_id AS InstanceId,status AS Status,
                processes_expected AS ProcessesExpected,processes_running AS ProcessesRunning,
                active_jobs AS ActiveJobs,queued_jobs AS QueuedJobs,
                succeeded_jobs AS SucceededJobs,retrying_jobs AS RetryingJobs,
                failed_jobs AS FailedJobs,blacklisted_symbols AS BlacklistedSymbols,
                cycles_completed AS CyclesCompleted,current_cycle_id AS CurrentCycleId,
                host_name AS HostName,app_version AS Version,started_at AS StartedAt,
                last_seen_at AS LastSeenAt,last_error AS LastError,
                last_error_at AS LastErrorAt,workers_json AS WorkersJson
            FROM pair_trend_collector_heartbeat ORDER BY last_seen_at DESC;

            SELECT COUNT(*) FROM pair_trend_symbol_blacklist
            WHERE expires_at > UTC_TIMESTAMP(6);

            SELECT symbol AS Symbol,collector_id AS CollectorId,
                failure_count AS FailureCount,reason AS Reason,
                blacklisted_at AS BlacklistedAt,expires_at AS ExpiresAt
            FROM pair_trend_symbol_blacklist
            WHERE expires_at > UTC_TIMESTAMP(6)
            ORDER BY blacklisted_at DESC LIMIT 20;
            """;

        await using var connection = connectionFactory.Create();
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            sql, cancellationToken: cancellationToken));
        var databaseUtcNow = await grid.ReadSingleAsync<DateTime>();
        var rows = (await grid.ReadAsync<HeartbeatRow>()).ToArray();
        var activeBlacklistCount = await grid.ReadSingleAsync<int>();
        var blacklists = (await grid.ReadAsync<CollectorBlacklistEntry>()).ToArray();
        var collectors = rows.Select(static row => new CollectorHeartbeatSnapshot(
            row.CollectorId,
            row.InstanceId,
            row.Status,
            row.ProcessesExpected,
            row.ProcessesRunning,
            row.ActiveJobs,
            row.QueuedJobs,
            row.SucceededJobs,
            row.RetryingJobs,
            row.FailedJobs,
            row.BlacklistedSymbols,
            row.CyclesCompleted,
            row.CurrentCycleId,
            row.HostName,
            row.Version,
            row.StartedAt,
            DateTime.SpecifyKind(row.LastSeenAt, DateTimeKind.Utc),
            row.LastError,
            row.LastErrorAt is null
                ? null
                : DateTime.SpecifyKind(row.LastErrorAt.Value, DateTimeKind.Utc),
            DeserializeWorkers(row.WorkersJson))).ToArray();
        return new CollectorOperationsSnapshot(
            DateTime.SpecifyKind(databaseUtcNow, DateTimeKind.Utc),
            collectors,
            activeBlacklistCount,
            blacklists);
    }

    private static IReadOnlyList<CollectorWorkerHeartbeat> DeserializeWorkers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<CollectorWorkerHeartbeat>();
        try
        {
            return JsonSerializer.Deserialize<CollectorWorkerHeartbeat[]>(json, JsonOptions)
                   ?? Array.Empty<CollectorWorkerHeartbeat>();
        }
        catch (JsonException)
        {
            return Array.Empty<CollectorWorkerHeartbeat>();
        }
    }

    private sealed class HeartbeatRow
    {
        public string CollectorId { get; init; } = "";
        public string InstanceId { get; init; } = "";
        public string Status { get; init; } = "";
        public int ProcessesExpected { get; init; }
        public int ProcessesRunning { get; init; }
        public int ActiveJobs { get; init; }
        public int QueuedJobs { get; init; }
        public long SucceededJobs { get; init; }
        public long RetryingJobs { get; init; }
        public long FailedJobs { get; init; }
        public int BlacklistedSymbols { get; init; }
        public long CyclesCompleted { get; init; }
        public string? CurrentCycleId { get; init; }
        public string? HostName { get; init; }
        public string? Version { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime LastSeenAt { get; init; }
        public string? LastError { get; init; }
        public DateTime? LastErrorAt { get; init; }
        public string? WorkersJson { get; init; }
    }
}
