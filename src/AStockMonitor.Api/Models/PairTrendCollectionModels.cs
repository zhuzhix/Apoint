namespace AStockMonitor.Api.Models;

/// <summary>Python 采集端执行一次四周期 K 线拉取前取得的严格计划。</summary>
public sealed record PairTrendCollectionPlanResponse(
    bool ShouldCollect,
    string? Reason,
    string? CycleId,
    DateOnly? TradingDate,
    string? Mode,
    IReadOnlyList<PairTrendCollectionWindow> Windows,
    IReadOnlyList<PairTrendCollectionSymbol> Symbols,
    int ExpectedSymbolCount);

public sealed record PairTrendCollectionWindow(string Frequency, DateTime From, DateTime To);

public sealed record PairTrendCollectionSymbol(string Symbol, string? Name);

/// <summary>Python 采集端向 API 推送的一批已闭合官方 K 线。</summary>
public sealed record PairTrendCollectionBatchRequest(
    IReadOnlyList<PairTrendCollectedBar> Bars);

public sealed record PairTrendCollectedBar(
    string Symbol,
    string Frequency,
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

/// <summary>
/// 只有计划中的全部证券和全部计划周期均完成，API 才接收本次结果进入内存回放队列。
/// 任何失败都必须显式上报，不能将水位推进到不完整数据之后。
/// </summary>
public sealed record PairTrendCollectionCompleteRequest(
    IReadOnlyList<string> CompletedSymbols,
    IReadOnlyList<PairTrendCollectionFailure>? Failures = null,
    IReadOnlyList<PairTrendCollectionSparseManifest>? SparseManifest = null);

public sealed record PairTrendCollectionFailure(string Symbol, string Frequency, string Error);

/// <summary>
/// 官方数据源对同一窗口执行一次原始查询和两次独立单股复核后，三次实际 K 线映射
/// 完全一致时，才可声明这些计划 EOB 确实没有官方成交 K 线。禁止合成或前值填充。
/// </summary>
public sealed record PairTrendCollectionSparseManifest(
    string Symbol,
    string Frequency,
    IReadOnlyList<DateTime> MissingEobs,
    int Confirmations);

public sealed record PairTrendCollectionAbortRequest(string Error);

public sealed record PairTrendCollectionAcceptedResponse(string CycleId, string Status);

public sealed record PairTrendCollectionStatusResponse(
    DateOnly? TradingDate,
    string Status,
    string? ActiveCycleId,
    DateTime? LastCompletedAt,
    string? LastError,
    IReadOnlyDictionary<string, DateTime> Watermarks,
    int SymbolsInMemory,
    long BarsInMemory,
    DateTime? LastErrorAt = null);

public sealed record AuthoritativeUniverseSyncRequest(
    string CollectorId,
    DateOnly TradingDate,
    bool IsTradingDay,
    string Source,
    DateTimeOffset SourceUpdatedAt,
    IReadOnlyList<AuthoritativeUniverseSymbolRequest>? Symbols);

public sealed record AuthoritativeUniverseSymbolRequest(
    string Symbol,
    string Name,
    bool IsSt,
    bool IsSuspended,
    DateOnly? ListDate,
    DateOnly? DelistDate);
