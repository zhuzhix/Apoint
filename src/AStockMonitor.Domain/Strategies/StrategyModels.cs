using AStockMonitor.Domain.Market;

namespace AStockMonitor.Domain.Strategies;

/// <summary>策略扫描档位。</summary>
public enum StrategyScanProfile { Fast, Observe, Event, Close, Replay }

/// <summary>策略给出的观察动作；本系统不产生交易或下单动作。</summary>
public enum StrategyAction { None, Watch, PullbackWait, Candidate, Confirm }

/// <summary>策略信号置信度。</summary>
public enum StrategyConfidence { None, Low, Medium, High }

/// <summary>策略信号生命周期事件。</summary>
public enum StrategySignalEventType
{
    New, Repeated, Strengthened, Weakened, Expired, Revised, Invalidated
}

/// <summary>多策略合并后的机会层级。</summary>
public enum StrategyOpportunityLevel { Observe, Candidate, Focus }

/// <summary>单个策略的注册信息和稳定版本。</summary>
public sealed record StrategyDescriptor(
    string Code,
    string Name,
    string Version,
    StrategyScanProfile Profile,
    IReadOnlyList<string> RequiredFrequencies,
    bool Enabled = true);

/// <summary>策略使用的统一价格 K 线，不暴露具体存储表。</summary>
public sealed record StrategyBar(
    string Frequency,
    DateTimeOffset Bob,
    DateTimeOffset Eob,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    decimal Amount,
    bool IsClosed = true,
    int Revision = 0,
    string RowHash = "");

/// <summary>从数据底座加载、尚未计算共享特征的原始快照。</summary>
public sealed record StrategySnapshotInput(
    string Symbol,
    DateOnly TradingDate,
    DateTimeOffset ObservedAt,
    LatestQuote? Quote,
    IReadOnlyList<StrategyBar> Minute1Bars,
    IReadOnlyList<StrategyBar> Minute30Bars,
    IReadOnlyList<StrategyBar> DailyBars,
    decimal? MarketAverageChangePercent,
    string SourceWatermark,
    bool DataReady,
    string? DataIssue = null,
    int IntradayBarMinutes = 1);

/// <summary>多个策略共享且只计算一次的价量技术特征。</summary>
public sealed record StrategyFeatures
{
    public decimal Price { get; init; }
    public decimal PreClose { get; init; }
    public decimal DayOpen { get; init; }
    public decimal DayHigh { get; init; }
    public decimal DayLow { get; init; }
    public decimal ChangePercent { get; init; }
    public decimal FromOpenPercent { get; init; }
    public decimal PullbackFromHighPercent { get; init; }
    public decimal IntradayClosePosition { get; init; }
    public decimal UpperWickRatio { get; init; }
    public decimal Vwap { get; init; }
    public decimal AboveVwapPercent { get; init; }
    public decimal Amount { get; init; }
    public long Volume { get; init; }
    public decimal VolumeRatio { get; init; }
    public decimal Last5MinuteReturnPercent { get; init; }
    public decimal VolumeAcceleration5 { get; init; }
    public decimal Recent3VolumeRatio { get; init; }
    public bool Recent3VolumeNonDecreasing { get; init; }
    public int ConsecutiveClosesAboveVwap { get; init; }
    public bool VwapPullbackRestart { get; init; }
    public decimal MinutePlatformHigh15 { get; init; }
    public decimal MinutePlatformBreakoutPercent { get; init; }
    public decimal Ma5 { get; init; }
    public decimal Ma10 { get; init; }
    public decimal Ma20 { get; init; }
    public decimal Ma30 { get; init; }
    public decimal Ma60 { get; init; }
    public decimal Ma20SlopePercent { get; init; }
    public decimal TrendStrengthPercent { get; init; }
    public decimal FiveDayReturnPercent { get; init; }
    public decimal PullbackVolumeRatio { get; init; }
    public decimal SupportPrice { get; init; }
    public decimal DistanceFromSupportPercent { get; init; }
    public decimal MarketAverageChangePercent { get; init; }
    public decimal RelativeMarketStrengthPercent { get; init; }
    public decimal WeekPlatformHigh24 { get; init; }
    public decimal WeekPlatformLow24 { get; init; }
    public decimal WeekRange24Percent { get; init; }
    public decimal WeekRange12Percent { get; init; }
    public int WeekPlatformTouches { get; init; }
    public decimal Latest30VolumeRatio { get; init; }
    public decimal Latest30ClosePosition { get; init; }
    public decimal Latest30UpperWickRatio { get; init; }
    public bool Latest30Bullish { get; init; }
    public decimal DeclineFromMajorHighPercent { get; init; }
    public decimal ReboundFromPrimaryLowPercent { get; init; }
    public decimal RepairFromSecondaryLowPercent { get; init; }
    public bool HasSecondaryBottom { get; init; }
    public bool BreaksDescendingTrend { get; init; }
}

/// <summary>策略规则的确定性输入。</summary>
public sealed record StrategySnapshot(
    StrategySnapshotInput Input,
    IReadOnlyList<StrategyBar> WeeklyBars,
    StrategyFeatures Features);

/// <summary>策略规则的确定性输出。</summary>
public sealed record StrategyEvaluation(
    string StrategyCode,
    string StrategyVersion,
    string Symbol,
    DateOnly TradingDate,
    DateTimeOffset ObservedAt,
    bool Qualified,
    StrategyAction Action,
    StrategyConfidence Confidence,
    decimal Score,
    decimal? StopReference,
    decimal? TargetReference,
    IReadOnlyList<string> PassedConditions,
    IReadOnlyList<string> FailedConditions,
    string SourceWatermark,
    string FeatureJson = "{}",
    string ParameterJson = "{}");

/// <summary>一次扫描任务的可持久化标识。</summary>
public sealed record StrategyScanRun(
    long Id,
    string RunKey,
    StrategyScanProfile Profile,
    string TriggerType,
    DateOnly TradingDate,
    DateTimeOffset StartedAt);
