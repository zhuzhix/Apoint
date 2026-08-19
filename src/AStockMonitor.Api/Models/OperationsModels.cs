using AStockMonitor.Application.Collection;
using System.Text.Json;

namespace AStockMonitor.Api.Models;

public sealed record CollectorHeartbeatRequest(
    string CollectorId,
    string InstanceId,
    string Status,
    int ProcessLimit,
    int? ProcessesExpected,
    int? ProcessesRunning,
    int ActiveProcesses,
    int ActiveJobs,
    int QueuedJobs,
    long SucceededSymbols,
    long RetryingJobs,
    long FailedSymbols,
    int BlacklistedSymbols,
    long CyclesCompleted,
    string? CurrentCycleId,
    string? HostName,
    string? Version,
    DateTime? StartedAt,
    string? LastError,
    IReadOnlyList<CollectorWorkerHeartbeatRequest>? Workers);

public sealed record CollectorWorkerHeartbeatRequest(
    JsonElement WorkerId,
    int? Pid,
    string State,
    int AssignedSymbols,
    int CompletedSymbols,
    int FailedSymbols,
    string? CurrentSymbol,
    string? LastError);

public sealed record CollectorBlacklistRequest(
    string CollectorId,
    string Symbol,
    string Reason,
    int FailureCount);

public sealed record CollectorHeartbeatAcceptedResponse(
    string Status,
    DateTime ServerTime);

public sealed record CollectorBlacklistResponse(
    string Symbol,
    DateTime BlacklistedAt,
    DateTime ExpiresAt,
    int FailureCount);

public sealed record OperationsStatusResponse(
    DateTime CheckedAt,
    string OverallStatus,
    OperationsCollectorStatus Collector,
    OperationsApiStatus Api,
    OperationsWebsiteStatus Website,
    PairTrendCollectionStatusResponse Collection,
    IReadOnlyList<OperationsCollectorInstance> Collectors,
    OperationsBlacklistStatus Blacklist,
    OperationsDatabaseStatus Database,
    IReadOnlyList<OperationsRecentError> RecentErrors);

public sealed record OperationsCollectorStatus(
    string Status,
    DateTime? LastHeartbeatAt,
    int ProcessesExpected,
    int ProcessesRunning,
    int ActiveJobs,
    int QueuedJobs,
    long SucceededJobs,
    long RetryingJobs,
    long FailedJobs,
    int BlacklistedSymbols,
    IReadOnlyList<OperationsCollectorProcess> Processes);

public sealed record OperationsCollectorInstance(
    string CollectorId,
    string InstanceId,
    string Status,
    string Health,
    DateTime LastHeartbeatAt,
    long HeartbeatAgeSeconds,
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
    IReadOnlyList<OperationsCollectorProcess> Processes,
    string? LastError);

public sealed record OperationsCollectorProcess(
    string WorkerId,
    int? Pid,
    string Status,
    int AssignedSymbols,
    int CompletedSymbols,
    int FailedSymbols,
    string? CurrentSymbol,
    string? LastError);

public sealed record OperationsApiStatus(
    string Status,
    string Service,
    string Version,
    long UptimeSeconds,
    double ResponseTimeMs,
    DateTime? LastErrorAt);

public sealed record OperationsWebsiteStatus(
    string Status,
    string Service,
    string Url,
    double ResponseTimeMs,
    DateTime LastCheckedAt,
    bool IndexFileExists,
    int StaticAssetCount);

public sealed record OperationsBlacklistStatus(
    int ActiveSymbols,
    IReadOnlyList<OperationsBlacklistEntry> Recent);

public sealed record OperationsBlacklistEntry(
    string Symbol,
    int FailureCount,
    string Reason,
    DateTime BlacklistedAt,
    DateTime ExpiresAt);

public sealed record OperationsDatabaseStatus(
    string Status,
    double ResponseTimeMs);

public sealed record OperationsRecentError(
    string Source,
    string Message,
    DateTime OccurredAt);
