namespace AStockMonitor.Api.Models;

/// <summary>对子事件的数据来源和运行上下文。</summary>
public sealed class PairTrendSourceInfo
{
    /// <summary>历史回放运行 ID；实时事件为空。</summary>
    public long? RunId { get; init; }

    /// <summary>运行模式，例如 realtime、historical 或 acceptance。</summary>
    public string RunMode { get; init; } = string.Empty;

    /// <summary>行情来源，例如 dongcai-gm 或 acceptance-fixture。</summary>
    public string DataSource { get; init; } = string.Empty;

    /// <summary>是否为算法验收样本，而不是真实 A 股行情。</summary>
    public bool IsAcceptanceSample { get; init; }

    /// <summary>运行备注。</summary>
    public string? Notes { get; init; }
}

/// <summary>对子详情默认展示的 K 线周期和时间窗口。</summary>
public sealed class PairTrendRecommendedChart
{
    /// <summary>默认周期：5m、30m、60m 或 1d。</summary>
    public string Frequency { get; init; } = "5m";

    /// <summary>建议 K 线开始时间。</summary>
    public DateTime From { get; init; }

    /// <summary>建议 K 线结束时间。</summary>
    public DateTime To { get; init; }

    /// <summary>根据事件时间和周期生成建议窗口。</summary>
    public static PairTrendRecommendedChart Create(
        string? frequency,
        DateTime firstSeenAt,
        DateTime lastSeenAt)
    {
        var normalized = frequency?.Trim().ToLowerInvariant() switch
        {
            "30m" => "30m",
            "60m" => "60m",
            "1d" => "1d",
            _ => "5m"
        };
        var (daysBefore, daysAfter) = normalized switch
        {
            "30m" => (30, 5),
            "60m" => (60, 10),
            "1d" => (120, 30),
            _ => (7, 2)
        };
        return new PairTrendRecommendedChart
        {
            Frequency = normalized,
            From = firstSeenAt.AddDays(-daysBefore),
            To = lastSeenAt.AddDays(daysAfter)
        };
    }
}
