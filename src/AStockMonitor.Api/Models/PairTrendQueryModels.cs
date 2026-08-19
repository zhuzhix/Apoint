namespace AStockMonitor.Api.Models;

public sealed record PairTrendCapabilitiesResponse(
    bool HistoricalDataEnabled,
    bool IntradayEnabled,
    bool HistoricalReplayEnabled,
    string TimeZone,
    int IntradayRefreshSeconds,
    int MaximumDateRangeDays);

public sealed record PairTrendStockGroupPage(
    int Page,
    int PageSize,
    long Total,
    long TotalPages,
    IReadOnlyCollection<PairTrendStockGroupDto> Groups);

public sealed class PairTrendStockGroupDto
{
    public string Symbol { get; init; } = string.Empty;
    public string? SymbolName { get; init; }
    public DateTime LatestPivotAt { get; init; }
    public DateTime? LatestTopAt { get; init; }
    public DateTime? LatestBottomAt { get; init; }
    public string LatestStageAtEnd { get; init; } = string.Empty;
    public long EventCount { get; init; }
    public long TopCount { get; init; }
    public long BottomCount { get; init; }
    public long ActiveAtEndCount { get; init; }
    public long InvalidatedAtEndCount { get; init; }
}

public sealed record PairTrendTimelinePage(
    int Page,
    int PageSize,
    long Total,
    long TotalPages,
    IReadOnlyCollection<PairTrendTimelineEventDto> Items,
    string? Symbol = null,
    string? SymbolName = null);

public sealed class PairTrendTimelineEventDto
{
    public long Id { get; init; }
    public string EventKey { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string? SymbolName { get; init; }
    public DateTime PivotAt { get; init; }
    public string PivotType { get; init; } = string.Empty;
    public decimal PairPrice { get; init; }
    public string PairKind { get; init; } = string.Empty;
    public int Generation { get; init; }
    public string Frequencies { get; init; } = string.Empty;
    public string StrongestFrequency { get; init; } = string.Empty;
    public string StageAtEnd { get; init; } = string.Empty;
    public bool IsActiveAtEnd { get; init; }
    public string CurrentStage { get; init; } = string.Empty;
    public bool CurrentIsActive { get; init; }
    public DateTime? ObservedAt { get; init; }
    public DateTime? FocusedAt { get; init; }
    public DateTime? EstablishedAt { get; init; }
    public DateTime? InvalidatedAt { get; init; }
    public string? InvalidationReason { get; init; }
    public DateTime? LastTransitionAt { get; init; }
    public string WaveCalculationStatus { get; init; } = "NOT_ELIGIBLE";
    public string? WaveSignal { get; init; }
    public int? WaveScore { get; init; }
    public DateTime? WaveEvaluatedAt { get; init; }
    public DateTime? WaveDataAsOf { get; init; }
    public string? WaveAlgorithmVersion { get; init; }
}

public sealed record PairTrendIntradayStatusResponse(
    DateOnly TradingDate,
    bool? IsTradingDay,
    string MarketDayStatus,
    string SessionStatus,
    string CollectionStatus,
    IReadOnlyDictionary<string, DateTime> Watermarks,
    DateTimeOffset CheckedAt,
    DateTimeOffset? LastUpdatedAt);

public class PairTrendGroupQuery
{
    public DateOnly? DateFrom { get; init; }
    public DateOnly? DateTo { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string? Keyword { get; init; }
    public string? PivotType { get; init; }
    public string? Frequency { get; init; }
    public string? StageAtEnd { get; init; }
    public bool? ActiveAtEnd { get; init; }
    public bool IncludeInvalidated { get; init; } = true;
}

public sealed class PairTrendEventQuery : PairTrendGroupQuery
{
    public string? Symbol { get; init; }
}

public sealed record PairTrendDateRange(
    DateOnly From,
    DateOnly To,
    DateTime FromInclusive,
    DateTime ToExclusive);

public sealed record PairTrendMarketDayRow(string Status, bool IsTradingDay, DateTime LastUpdatedAt);
