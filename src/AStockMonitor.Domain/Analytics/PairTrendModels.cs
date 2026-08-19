namespace AStockMonitor.Domain.Analytics;

/// <summary>对子趋势候选代表的阶段顶部或阶段底部。</summary>
public enum PairPivotType
{
    /// <summary>上升趋势中由 K 线最高价触发的顶部候选。</summary>
    Top,
    /// <summary>下降趋势中由 K 线最低价触发的底部候选。</summary>
    Bottom
}

/// <summary>候选出现前、仅使用已完成 K 线判断的趋势方向。</summary>
public enum PairTrendDirection
{
    /// <summary>预热数据不足，无法判断。</summary>
    Unknown,
    /// <summary>EMA 多头排列且快线向上。</summary>
    Up,
    /// <summary>EMA 空头排列且快线向下。</summary>
    Down,
    /// <summary>不满足明确上升或下降条件。</summary>
    Range
}

/// <summary>单根 K 线对子命中的确认状态。</summary>
public enum PairHitStatus
{
    /// <summary>已发现候选，但未来已完成 K 线数量还不足以确认。</summary>
    Candidate,
    /// <summary>在确认窗口内出现满足规则的反转。</summary>
    Confirmed,
    /// <summary>确认前出现更高高点或更低低点，候选失效。</summary>
    Invalidated
}

/// <summary>归并事件的总体状态。</summary>
public enum PairEventStatus
{
    /// <summary>事件只有待确认命中。</summary>
    Candidate,
    /// <summary>事件至少包含一条已确认命中。</summary>
    Confirmed,
    /// <summary>事件所有命中均已失效。</summary>
    Invalidated
}

/// <summary>同一对子价位在四个官方周期中的逐级状态。</summary>
public enum PairTrendStage
{
    Discovered = 1,
    Observing = 2,
    Focus = 3,
    Established = 4,
    Invalidated = 9
}

/// <summary>对子尾数的业务分类。</summary>
public enum PairPriceKind
{
    /// <summary>小数点后为 .00。</summary>
    Round00,
    /// <summary>小数点后为 .11、.22、…、.99。</summary>
    DoubleDigit
}

/// <summary>
/// 算法使用的标准 K 线。价格使用 decimal 保持两位价格精度，SourceRowHash 用于结果追溯。
/// </summary>
public sealed record PairTrendBar(
    string Symbol,
    string Frequency,
    DateTime TradingDate,
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

/// <summary>价格转换为整数 tick 后得到的对子匹配结果。</summary>
public sealed record PairPriceMatch(
    decimal Price,
    long PriceTicks,
    int PairCode,
    PairPriceKind Kind);

/// <summary>
/// 单根 K 线产生的完整对子命中结果，包含候选时可获得的趋势指标、K 线特征和延迟确认结果。
/// </summary>
public sealed record PairTrendHitResult(
    string HitKey,
    string Symbol,
    string Frequency,
    DateTime TradingDate,
    DateTime Bob,
    DateTime Eob,
    DateTime ObservedAt,
    DateTime? ConfirmedAt,
    PairPivotType PivotType,
    PairHitStatus Status,
    decimal PairPrice,
    long PriceTicks,
    int PairCode,
    PairPriceKind PairKind,
    string HitField,
    PairTrendDirection TrendDirection,
    decimal TrendStrength,
    decimal Ema20,
    decimal Ema60,
    decimal Atr14,
    decimal? PreviousClose,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    long Volume,
    decimal Amount,
    bool IsRollingExtreme,
    decimal VolumePercentile,
    decimal WickRatio,
    decimal ReversalAtr,
    decimal Score,
    string? ConfirmationReason,
    string SourceRowHash,
    string AlgorithmVersion,
    PairTrendStage Stage = PairTrendStage.Discovered,
    bool IsPromotion = false);

/// <summary>事件状态变化审计；回测和实时链路使用相同原因码。</summary>
public sealed record PairTrendLifecycleResult(
    string LifecycleKey,
    PairTrendStage? FromStage,
    PairTrendStage ToStage,
    DateTime OccurredAt,
    string TriggerFrequency,
    decimal TriggerPrice,
    string Reason,
    string SourceRowHash,
    bool ShouldNotify);

/// <summary>
/// 同股票、同顶底方向、同事件窗口的归并结果；Hits 保留所有周期和所有 K 线明细。
/// </summary>
public sealed record PairTrendEventResult(
    string EventKey,
    string Symbol,
    string? SymbolName,
    PairPivotType PivotType,
    PairEventStatus Status,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    DateTime? ConfirmedAt,
    decimal LatestPairPrice,
    int LatestPairCode,
    PairPriceKind LatestPairKind,
    int TimeframeMask,
    string Frequencies,
    string StrongestFrequency,
    int ConfluenceCount,
    int TotalHitCount,
    int ConfirmedHitCount,
    int InvalidatedHitCount,
    int PendingHitCount,
    int Round00HitCount,
    int DoubleDigitHitCount,
    decimal Score,
    decimal MaxTrendStrength,
    string AlgorithmVersion,
    IReadOnlyList<PairTrendHitResult> Hits,
    PairTrendStage Stage = PairTrendStage.Discovered,
    long PriceTicks = 0,
    int Generation = 1,
    bool IsActive = true,
    DateTime? ObservedAt = null,
    DateTime? FocusedAt = null,
    DateTime? EstablishedAt = null,
    DateTime? InvalidatedAt = null,
    decimal? InvalidatedPrice = null,
    string? InvalidationReason = null,
    DateTime? RootFiveMinuteBob = null,
    DateTime? RootFiveMinuteEob = null,
    IReadOnlyList<PairTrendLifecycleResult>? Lifecycles = null);

/// <summary>单只股票在一次回测中的处理结果。</summary>
public sealed record PairTrendSymbolResult(
    string Symbol,
    long BarsProcessed,
    IReadOnlyList<PairTrendEventResult> Events)
{
    /// <summary>该股票所有归并事件包含的 K 线命中数量。</summary>
    public int HitsDetected => Events.Sum(static item => item.Hits.Count);
}

/// <summary>一次对子趋势历史回测的请求参数。</summary>
public sealed record PairTrendBacktestRequest(
    DateOnly DateFrom,
    DateOnly DateTo,
    IReadOnlyList<string> Frequencies,
    int? SymbolLimit,
    bool Force,
    string RunMode = "historical",
    string DataSource = "dongcai-gm",
    string? Notes = null,
    IReadOnlyList<string>? Symbols = null);

/// <summary>一次对子趋势历史回测的运行摘要。</summary>
public sealed record PairTrendBacktestResult(
    long RunId,
    string RunKey,
    string Status,
    int RequestedSymbols,
    int CompletedSymbols,
    int FailedSymbols,
    long BarsProcessed,
    long HitsDetected,
    long EventsWritten);
