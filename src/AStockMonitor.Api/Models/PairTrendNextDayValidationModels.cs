namespace AStockMonitor.Api.Models;

public sealed record NextDayValidationCreateRunRequest(
    DateOnly DateFrom,
    DateOnly DateTo,
    bool ApplyChanges,
    IReadOnlyCollection<DateOnly> TradingDates);

public sealed record NextDayValidationRunResponse(
    long RunId,
    string Status,
    DateOnly DateFrom,
    DateOnly DateTo,
    bool ApplyChanges,
    int Total,
    int Completed,
    int Invalidated,
    int Passed,
    int NoTrade,
    int NotApplicable,
    int Failed,
    string? LastError = null);

public sealed record NextDayValidationClaimResponse(
    long RunId,
    string? LeaseToken,
    DateOnly? ValidationTradingDate,
    bool ApplyChanges,
    IReadOnlyCollection<string> Symbols,
    int MaximumSymbols,
    int MaximumBarsPerBatch);

public sealed record NextDayValidationFiveMinuteBar(
    string Symbol,
    DateTime Bob,
    DateTime Eob,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    decimal? PreClose,
    long Volume,
    decimal Amount,
    string SourceRowHash);

public sealed record NextDayValidationBatchRequest(
    string LeaseToken,
    IReadOnlyCollection<NextDayValidationFiveMinuteBar> Bars);

public sealed record NextDayValidationSparseProof(
    string Symbol,
    IReadOnlyCollection<DateTime> MissingEobs,
    int Confirmations);

public sealed record NextDayValidationSymbolFailure(string Symbol, string Error);

public sealed record NextDayValidationCompleteRequest(
    string LeaseToken,
    IReadOnlyCollection<NextDayValidationSparseProof> SparseProofs,
    IReadOnlyCollection<NextDayValidationSymbolFailure>? Failures = null);

public sealed record NextDayValidationFailLeaseRequest(
    string LeaseToken,
    string Error,
    bool ProviderUnavailable = false);

public sealed record NextDayValidationAcceptedResponse(
    string Status,
    int Accepted,
    int Completed,
    int Retrying,
    int Failed);
