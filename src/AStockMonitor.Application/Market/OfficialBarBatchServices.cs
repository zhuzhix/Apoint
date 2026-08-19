namespace AStockMonitor.Application.Market;

public sealed record OfficialBarBatchInput(
    Guid CommandId,
    Guid BatchId,
    string GatewayId,
    string WorkerId,
    long RecoveryItemId,
    IReadOnlyCollection<CanonicalBarInput> Bars);

public sealed record OfficialBarBatchWriteResult(
    bool Applied,
    int AcceptedCount,
    int DuplicateCount,
    int RejectedCount,
    string? Reason = null);

/// <summary>
/// The only ingress for official closed bars. It owns the durable server-side
/// batch receipt, canonical writes, recovery progress and replay-task creation.
/// </summary>
public interface IOfficialBarBatchWriter
{
    Task<OfficialBarBatchWriteResult> WriteAsync(
        OfficialBarBatchInput input,
        CancellationToken cancellationToken);
}
