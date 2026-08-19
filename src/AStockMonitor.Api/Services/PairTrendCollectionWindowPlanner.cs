using AStockMonitor.Api.Models;

namespace AStockMonitor.Api.Services;

/// <summary>纯函数形式生成已跨过供应商发布宽限期的四周期窗口。</summary>
internal static class PairTrendCollectionWindowPlanner
{
    internal static IReadOnlyList<PairTrendCollectionWindow> BuildAvailableWindows(
        DateOnly tradingDate,
        DateTime localNow,
        TimeSpan completedBarGrace,
        TimeSpan dailyBarGrace)
    {
        var open = tradingDate.ToDateTime(new TimeOnly(9, 30));
        // 只有当日新闭合 K 线应用发布宽限。历史补算必须完整暴露全日窗口。
        var isCurrentTradingDate = tradingDate == DateOnly.FromDateTime(localNow);
        var closedWatermark = isCurrentTradingDate
            ? localNow.Subtract(completedBarGrace)
            : tradingDate.ToDateTime(TimeOnly.MaxValue);
        var dailyClosedWatermark = isCurrentTradingDate
            ? localNow.Subtract(dailyBarGrace)
            : tradingDate.ToDateTime(TimeOnly.MaxValue);
        var windows = new List<PairTrendCollectionWindow>();
        AddIfClosed("5m", LastClosed(tradingDate, closedWatermark, FiveMinuteCloses()), open, windows);
        AddIfClosed("30m", LastClosed(tradingDate, closedWatermark, ThirtyMinuteCloses()), open, windows);
        AddIfClosed("60m", LastClosed(tradingDate, closedWatermark, SixtyMinuteCloses()), open, windows);
        // 日 K 的供应商落地时间与分钟 K 不同，不能复用 90 秒闭合宽限。
        // 独立宽限只负责避免过早请求；采集端仍会对实际供应商可用性做硬门禁。
        AddIfClosed("1d", LastClosed(tradingDate, dailyClosedWatermark, [new TimeOnly(15, 0)]), open, windows);
        return windows;
    }

    private static void AddIfClosed(
        string frequency,
        DateTime? closedAt,
        DateTime open,
        ICollection<PairTrendCollectionWindow> target)
    {
        if (closedAt is not null)
            target.Add(new PairTrendCollectionWindow(frequency, open, closedAt.Value));
    }

    private static DateTime? LastClosed(
        DateOnly tradingDate,
        DateTime watermark,
        IEnumerable<TimeOnly> closes)
    {
        var values = closes.Select(tradingDate.ToDateTime)
            .Where(value => value <= watermark)
            .ToArray();
        return values.Length == 0 ? null : values[^1];
    }

    private static IEnumerable<TimeOnly> FiveMinuteCloses()
    {
        for (var value = new TimeOnly(9, 35); value <= new TimeOnly(11, 30); value = value.AddMinutes(5))
            yield return value;
        for (var value = new TimeOnly(13, 5); value <= new TimeOnly(15, 0); value = value.AddMinutes(5))
            yield return value;
    }

    private static IEnumerable<TimeOnly> ThirtyMinuteCloses() =>
        [new(10, 0), new(10, 30), new(11, 0), new(11, 30), new(13, 30), new(14, 0), new(14, 30), new(15, 0)];

    private static IEnumerable<TimeOnly> SixtyMinuteCloses() =>
        [new(10, 30), new(11, 30), new(14, 0), new(15, 0)];
}
