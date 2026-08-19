namespace AStockMonitor.Application.Market;

/// <summary>Normalized official SDK bar accepted by the canonical writer.</summary>
public sealed record CanonicalBarInput(
    string EventId,
    string Symbol,
    string Frequency,
    DateOnly TradingDate,
    DateTimeOffset Bob,
    DateTimeOffset Eob,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    decimal? PreClose,
    long Volume,
    decimal Amount,
    bool IsClosed,
    DateTimeOffset SourceUpdatedAt,
    string Source,
    string RowHash,
    string CollectionMode,
    long? RecoveryRunId = null);

public sealed record CanonicalBarWriteResult(
    bool Persisted,
    bool Changed,
    string? EventType,
    int Revision,
    string? StreamId = null);

/// <summary>
/// Atomically persists official closed bars together with synchronization and
/// reliable event-outbox state. Unclosed bars remain Redis projections only.
/// </summary>
public interface ICanonicalBarWriter
{
    Task<CanonicalBarWriteResult> WriteAsync(
        CanonicalBarInput input,
        CancellationToken cancellationToken);
}
