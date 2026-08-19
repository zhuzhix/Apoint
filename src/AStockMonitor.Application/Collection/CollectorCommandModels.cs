namespace AStockMonitor.Application.Collection;

public sealed class CollectorControlOptions
{
    public bool Enabled { get; set; } = true;
    public string GatewayId { get; set; } = "default";
    public int CommandTimeoutSeconds { get; set; } = 900;
    public int HistorySymbolsPerCommand { get; set; } = 100;
}

public sealed record CollectorCommand(
    Guid CommandId,
    string GatewayId,
    string? WorkerId,
    string CommandType,
    string PayloadJson,
    string Status,
    int AttemptCount,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    string? LastError);

public sealed record HistoryCollectionCommand(
    Guid CommandId,
    long RecoveryRunId,
    IReadOnlyCollection<long> RecoveryItemIds,
    IReadOnlyCollection<string> Symbols,
    string Frequency,
    DateTimeOffset Start,
    DateTimeOffset End);

public sealed record TickSubscriptionAssignment(
    string WorkerId,
    string AssignmentVersion,
    IReadOnlyCollection<string> Symbols);

public interface ICollectorCommandRepository
{
    Task<int> CreateHistoryCommandsAsync(
        long recoveryRunId,
        string gatewayId,
        int symbolsPerCommand,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<Guid?> CreateSnapshotCommandAsync(
        string gatewayId,
        IReadOnlyCollection<string> symbols,
        string universeVersion,
        int staleSeconds,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<int> ReplaceTickAssignmentsAsync(
        string gatewayId,
        IReadOnlyCollection<TickSubscriptionAssignment> assignments,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<CollectorCommand>> ClaimPendingAsync(
        string gatewayId,
        int maxCount,
        CancellationToken cancellationToken);

    Task MarkAcknowledgedAsync(
        Guid commandId,
        string gatewayId,
        CancellationToken cancellationToken);

    Task MarkCompletedAsync(
        Guid commandId,
        string gatewayId,
        CancellationToken cancellationToken);

    Task MarkFailedAsync(
        Guid commandId,
        string gatewayId,
        string error,
        CancellationToken cancellationToken);

    Task RecordGatewayHeartbeatAsync(
        string gatewayId,
        string displayName,
        int protocolVersion,
        string status,
        string? error,
        CancellationToken cancellationToken);
}
