namespace AStockMonitor.Application.Recovery;

public interface IMarketRecoveryRepository
{
    Task<IReadOnlyCollection<EligibleInstrumentDay>> GetEligibleInstrumentDaysAsync(
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<string>? symbols,
        CancellationToken cancellationToken);

    Task<IReadOnlySet<DateTime>> GetExistingBarEndsAsync(
        string symbol,
        DateOnly tradingDate,
        string frequency,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, IReadOnlySet<DateTime>>> GetExistingBarEndsAsync(
        IReadOnlyCollection<string> symbols,
        DateOnly tradingDate,
        string frequency,
        CancellationToken cancellationToken);

    Task<MarketRecoveryRunRecord> BeginDetectionRunAsync(
        MarketGapDetectionRequest request,
        int requestedSymbols,
        int overlapSeconds,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<MarketGapRecord>> SaveDetectedGapsAsync(
        long runId,
        IReadOnlyCollection<DetectedMarketGap> gaps,
        bool createRecoveryItems,
        CancellationToken cancellationToken);

    Task<MarketRecoveryRunRecord> FinishDetectionRunAsync(
        long runId,
        string status,
        long gapCount,
        string? resultJson,
        string? error,
        CancellationToken cancellationToken);

    Task<PagedResult<MarketGapRecord>> QueryGapsAsync(
        int page,
        int pageSize,
        string? status,
        string? symbol,
        string? dataset,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        CancellationToken cancellationToken);

    Task<PagedResult<MarketRecoveryRunRecord>> QueryRunsAsync(
        int page,
        int pageSize,
        string? status,
        CancellationToken cancellationToken);

    Task<MarketRecoveryRunRecord?> GetRunAsync(long id, CancellationToken cancellationToken);
    Task<MarketRecoveryRunRecord?> GetLatestRunAsync(
        DateOnly tradingDate,
        string triggerType,
        CancellationToken cancellationToken);
    Task<bool> CancelRunAsync(
        long id,
        string reason,
        string requestedBy,
        CancellationToken cancellationToken);
    Task<bool> RetryRunAsync(long id, CancellationToken cancellationToken);
    Task<RecoveryStrategyReplayWork?> TryClaimStrategyReplayAsync(
        CancellationToken cancellationToken);
    Task CompleteStrategyReplayAsync(
        long runId,
        long eventsWritten,
        string? error,
        CancellationToken cancellationToken);
}
