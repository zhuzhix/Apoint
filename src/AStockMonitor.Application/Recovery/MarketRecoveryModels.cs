namespace AStockMonitor.Application.Recovery;

/// <summary>行情缺口检测与补数服务配置。</summary>
public sealed class MarketRecoveryOptions
{
    public bool Enabled { get; set; } = true;
    public bool RunOnStartup { get; set; } = true;
    public int IntradayDetectionSeconds { get; set; } = 30;
    public int CompletedBarGraceSeconds { get; set; } = 90;
    public int LiveOverlapSeconds { get; set; } = 120;
    public int LookbackTradingDaysOnStartup { get; set; } = 5;
    public int MaxConcurrentWorkersDuringMarket { get; set; } = 2;
    public int MaxConcurrentWorkersAfterMarket { get; set; } = 8;
    public int SymbolsPerWorker { get; set; } = 100;
    public int MaxRetries { get; set; } = 5;
    public string[] Datasets { get; set; } = ["5m", "30m", "60m", "1d"];
    public int IntradayHistoryLimitDays { get; set; } = 60;
    public bool OfficialBarBackfillEnabled { get; set; } = true;
    public bool StrategyReplayEnabled { get; set; } = true;
}

/// <summary>缺口检测请求；DryRun只检测，不创建可执行补数项目。</summary>
public sealed record MarketGapDetectionRequest(
    DateOnly DateFrom,
    DateOnly DateTo,
    IReadOnlyCollection<string>? Symbols,
    IReadOnlyCollection<string>? Datasets,
    IReadOnlyCollection<string>? DetectTypes = null,
    bool DryRun = true,
    string TriggerType = "manual",
    DateTime? CompletedBefore = null);

public sealed record MarketGapRecord(
    long Id,
    string GapKey,
    string Symbol,
    string Dataset,
    string? Frequency,
    DateOnly TradingDate,
    DateTime GapStart,
    DateTime GapEnd,
    string DetectMethod,
    string Status,
    string Severity,
    int ExpectedCount,
    int LocalCount,
    int RecoveredCount,
    int MissingCount,
    bool? TickRecoverable,
    string? RecoverySource,
    long? RecoveryRunId,
    int RetryCount,
    string? LastError,
    DateTime DetectedAt,
    DateTime? CompletedAt);

public sealed record MarketRecoveryRunRecord(
    long Id,
    string RunKey,
    string TriggerType,
    string Status,
    DateOnly DateFrom,
    DateOnly DateTo,
    DateTime? CutoverTime,
    int OverlapSeconds,
    bool DryRun,
    int RequestedSymbols,
    int CompletedSymbols,
    int FailedSymbols,
    long GapsDetected,
    long BarsDownloaded,
    long BarsInserted,
    long BarsRevised,
    long TicksReplayed,
    long QualityIssueCount,
    long StrategyEventsRecalculated,
    string? ErrorMessage,
    DateTime StartedAt,
    DateTime? FinishedAt);

public sealed record MarketGapDetectionResult(
    MarketRecoveryRunRecord Run,
    IReadOnlyCollection<MarketGapRecord> Gaps,
    int EligibleSymbolDays,
    long ExpectedSlots,
    long ExistingSlots);

public sealed record PagedResult<T>(
    int Page,
    int PageSize,
    long Total,
    int TotalPages,
    IReadOnlyCollection<T> Items);

public sealed record EligibleInstrumentDay(string Symbol, DateOnly TradingDate);

public sealed record DetectedMarketGap(
    string GapKey,
    string Symbol,
    string Dataset,
    string Frequency,
    DateOnly TradingDate,
    DateTime GapStart,
    DateTime GapEnd,
    int ExpectedCount,
    int LocalCount,
    int MissingCount,
    string Severity);

public sealed record RecoveryStrategyReplayWork(
    long RunId,
    DateOnly DateFrom,
    DateOnly DateTo,
    IReadOnlyCollection<string> Symbols);
