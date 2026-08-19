namespace AStockMonitor.Application.Collection;

/// <summary>采集端运行状态和失败隔离的非敏感配置。</summary>
public sealed class CollectorOperationsOptions
{
    public int ExpectedProcesses { get; set; } = 6;
    public int HeartbeatOfflineSeconds { get; set; } = 45;
    public int BlacklistHours { get; set; } = 24;
}

public sealed record CollectorWorkerHeartbeat(
    string WorkerId,
    int? Pid,
    string Status,
    int AssignedSymbols,
    int CompletedSymbols,
    int FailedSymbols,
    string? CurrentSymbol,
    string? LastError);

public sealed record CollectorHeartbeat(
    string CollectorId,
    string InstanceId,
    string Status,
    int ProcessesExpected,
    int ProcessesRunning,
    int ActiveJobs,
    int QueuedJobs,
    long SucceededJobs,
    long RetryingJobs,
    long FailedJobs,
    int BlacklistedSymbols,
    long CyclesCompleted,
    string? CurrentCycleId,
    string? HostName,
    string? Version,
    DateTime? StartedAt,
    string? LastError,
    IReadOnlyList<CollectorWorkerHeartbeat> Processes);

public sealed record CollectorHeartbeatSnapshot(
    string CollectorId,
    string InstanceId,
    string Status,
    int ProcessesExpected,
    int ProcessesRunning,
    int ActiveJobs,
    int QueuedJobs,
    long SucceededJobs,
    long RetryingJobs,
    long FailedJobs,
    int BlacklistedSymbols,
    long CyclesCompleted,
    string? CurrentCycleId,
    string? HostName,
    string? Version,
    DateTime? StartedAt,
    DateTime LastSeenAt,
    string? LastError,
    DateTime? LastErrorAt,
    IReadOnlyList<CollectorWorkerHeartbeat> Processes);

public sealed record CollectorBlacklistEntry(
    string Symbol,
    string CollectorId,
    int FailureCount,
    string Reason,
    DateTime BlacklistedAt,
    DateTime ExpiresAt);

public sealed record CollectorOperationsSnapshot(
    DateTime DatabaseUtcNow,
    IReadOnlyList<CollectorHeartbeatSnapshot> Collectors,
    int ActiveBlacklistCount,
    IReadOnlyList<CollectorBlacklistEntry> RecentBlacklists);

public interface ICollectorOperationsRepository
{
    Task RecordHeartbeatAsync(CollectorHeartbeat heartbeat, CancellationToken cancellationToken);

    Task<CollectorBlacklistEntry> BlacklistAsync(
        string collectorId,
        string symbol,
        int failureCount,
        string reason,
        TimeSpan duration,
        CancellationToken cancellationToken);

    Task<IReadOnlySet<string>> GetActiveBlacklistedSymbolsAsync(
        CancellationToken cancellationToken);

    Task<CollectorOperationsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
