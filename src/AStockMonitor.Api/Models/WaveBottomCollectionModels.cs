namespace AStockMonitor.Api.Models;

public sealed record WaveBottomClaimResponse(
    string? LeaseToken,
    IReadOnlyCollection<WaveBottomClaimJob> Jobs,
    int MaximumSymbols,
    int MaximumBarsPerBatch);

public sealed record WaveBottomClaimJob(
    long JobId,
    long EventId,
    string Symbol,
    DateTime FocusedAt,
    DateOnly DataEndDate,
    int RequiredDailyBars,
    string AdjustMode,
    string AlgorithmVersion);

public sealed record WaveBottomDailyBar(
    string Symbol,
    DateOnly TradingDate,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    decimal? PreClose,
    long Volume,
    decimal Amount,
    string SourceRowHash);

public sealed record WaveBottomBatchRequest(
    string LeaseToken,
    IReadOnlyCollection<WaveBottomDailyBar> Bars);

public sealed record WaveBottomSymbolFailure(string Symbol, string Error);

public sealed record WaveBottomCompleteRequest(
    string LeaseToken,
    IReadOnlyCollection<WaveBottomSymbolFailure>? Failures = null);

public sealed record WaveBottomLeaseFailureRequest(
    string LeaseToken,
    string Error,
    bool ProviderUnavailable = false);

public sealed record WaveBottomAcceptedResponse(
    string Status,
    int Accepted,
    int Completed,
    int Retrying,
    int Failed);
