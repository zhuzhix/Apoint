using AStockMonitor.Domain.Market;

namespace AStockMonitor.Application.Market;

/// <summary>Result of appending one market event to the reliable event log.</summary>
public sealed record DurablePublishReceipt(bool Appended, string? StreamId, string? Reason = null);

/// <summary>A shard-local Tick batch sent to one atomic Redis operation.</summary>
public sealed record TickPublishBatch(
    string BatchId,
    int ShardId,
    IReadOnlyList<TickEvent> Ticks);

/// <summary>Result of one atomic shard batch append.</summary>
public sealed record DurableBatchPublishReceipt(
    bool Appended,
    int AcceptedCount,
    int DuplicateCount,
    int ExpiredCount,
    int RejectedCount,
    string? LastStreamId,
    string? Reason = null);

/// <summary>
/// Appends normalized market events to a replayable log. Implementations must
/// never report Appended until the log server has accepted the event.
/// </summary>
public interface IReliableTickPublisher
{
    Task<DurablePublishReceipt> PublishAsync(TickEvent tick, CancellationToken cancellationToken);

    Task<DurableBatchPublishReceipt> PublishBatchAsync(
        TickPublishBatch batch,
        CancellationToken cancellationToken);
}
