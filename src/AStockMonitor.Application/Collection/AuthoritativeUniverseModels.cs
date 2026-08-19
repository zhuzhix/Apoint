namespace AStockMonitor.Application.Collection;

public sealed class AuthoritativeUniverseOptions
{
    public int MinimumTradingDaySymbols { get; set; } = 4_000;
    public int MinimumEligibleTradingDaySymbols { get; set; } = 4_500;
    public int MaximumTradingDaySymbols { get; set; } = 10_000;
    public int SourceFreshnessHours { get; set; } = 24;
    public int MaximumHistoricalBackfillDays { get; set; } = 7;
}

public sealed record AuthoritativeUniverseSymbol(
    string Symbol,
    string Name,
    string Exchange,
    bool IsSt,
    bool IsSuspended,
    bool IsEligible,
    DateOnly? ListDate,
    DateOnly? DelistDate);

public sealed record AuthoritativeUniverseSubmission(
    string CollectorId,
    DateOnly TradingDate,
    bool IsTradingDay,
    string Source,
    DateTime SourceUpdatedAtUtc,
    string UniverseVersion,
    string PayloadHash,
    IReadOnlyList<AuthoritativeUniverseSymbol> Symbols);

public sealed record AuthoritativeUniverseSyncResult(
    string Status,
    DateOnly TradingDate,
    bool IsTradingDay,
    int TotalSymbols,
    int EligibleSymbols,
    string UniverseVersion,
    string PayloadHash,
    DateTime SyncedAt);

public sealed record AuthoritativeUniverseSyncStatus(
    DateOnly TradingDate,
    string Status,
    bool IsTradingDay,
    int TotalSymbols,
    int EligibleSymbols,
    int ActualSymbols,
    int ActualEligibleSymbols,
    int MatchingSymbols,
    int MatchingEligibleSymbols,
    string UniverseVersion,
    string PayloadHash,
    DateTime SyncedAt)
{
    public bool IsReady =>
        string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase) &&
        ActualSymbols == TotalSymbols &&
        ActualEligibleSymbols == EligibleSymbols &&
        MatchingSymbols == TotalSymbols &&
        MatchingEligibleSymbols == EligibleSymbols &&
        (IsTradingDay ? TotalSymbols > 0 : TotalSymbols == 0);
}

public interface IAuthoritativeUniverseRepository
{
    Task<AuthoritativeUniverseSyncResult> SynchronizeAsync(
        AuthoritativeUniverseSubmission submission,
        CancellationToken cancellationToken);

    Task<AuthoritativeUniverseSyncStatus?> GetStatusAsync(
        DateOnly tradingDate,
        CancellationToken cancellationToken);
}
