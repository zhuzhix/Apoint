namespace AStockMonitor.Domain.Analytics;

/// <summary>跨周期同价存活确认算法的可复现参数。</summary>
public sealed record PairTrendOptions
{
    /// <summary>当前算法版本；V3 不使用 EMA/ATR 作为候选或确认门槛。</summary>
    public const string CurrentAlgorithmVersion = "pair-trend-v3";

    public string AlgorithmVersion { get; init; } = CurrentAlgorithmVersion;

    /// <summary>A 股默认最小价格变动单位。</summary>
    public decimal PriceTick { get; init; } = 0.01m;

    /// <summary>是否识别 .00。</summary>
    public bool IncludeRound00 { get; init; } = true;

    /// <summary>历史回放用后续 5 分钟 High/Low 代替未留存的历史 Tick 判断突破。</summary>
    public bool UseFiveMinuteExtremesForHistoricalBreak { get; init; } = true;

    /// <summary>没有业务指定的自动过期规则；候选只因严格突破而失效。</summary>
    public bool ExpireWithoutBreak { get; init; } = false;

    public void Validate()
    {
        if (PriceTick <= 0)
            throw new ArgumentOutOfRangeException(nameof(PriceTick));
        if (!UseFiveMinuteExtremesForHistoricalBreak)
            throw new ArgumentException("V3 historical replay requires 5m extremes because historical Tick is not retained.");
        if (ExpireWithoutBreak)
            throw new ArgumentException("V3 does not define time-based expiry.");
    }
}
