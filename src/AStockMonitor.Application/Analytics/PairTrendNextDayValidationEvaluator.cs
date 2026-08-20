using System.Security.Cryptography;
using System.Text;

namespace AStockMonitor.Application.Analytics;

public sealed record PairTrendNextDayBar(
    DateTime Eob,
    decimal HighPrice,
    decimal LowPrice,
    string SourceRowHash);

public sealed record PairTrendNextDayEvaluation(
    string Status,
    decimal? ObservedExtremePrice,
    DateTime? BreachedAt,
    decimal? BreachPrice,
    string SourceInputHash,
    int BarCount,
    int VerifiedMissingCount);

/// <summary>
/// 纯内存执行“成立后的下一交易日”价格验证。TOP 只在最高价严格大于对子价时失效，
/// BOTTOM 只在最低价严格小于对子价时失效；相等不失效。
/// </summary>
public static class PairTrendNextDayValidationEvaluator
{
    public const int ExpectedFiveMinuteBars = 48;

    public static PairTrendNextDayEvaluation Evaluate(
        string pivotType,
        decimal pairPrice,
        IReadOnlyCollection<PairTrendNextDayBar> bars,
        IReadOnlyCollection<DateTime> verifiedMissingEobs)
    {
        pivotType = pivotType.Trim().ToUpperInvariant();
        if (pivotType is not ("TOP" or "BOTTOM"))
            throw new ArgumentException("pivotType 必须是 TOP 或 BOTTOM。", nameof(pivotType));
        if (pairPrice <= 0) throw new ArgumentOutOfRangeException(nameof(pairPrice));
        if (bars.Count + verifiedMissingEobs.Count != ExpectedFiveMinuteBars)
            throw new InvalidOperationException(
                $"5分钟K线守恒失败：bars={bars.Count},missing={verifiedMissingEobs.Count},expected={ExpectedFiveMinuteBars}。");

        var ordered = bars.OrderBy(static item => item.Eob).ToArray();
        if (ordered.Select(static item => item.Eob).Distinct().Count() != ordered.Length)
            throw new InvalidOperationException("5分钟K线包含重复EOB。");
        if (verifiedMissingEobs.Distinct().Count() != verifiedMissingEobs.Count ||
            ordered.Select(static item => item.Eob).Intersect(verifiedMissingEobs).Any())
            throw new InvalidOperationException("已收K线与无成交证明不互斥。" );

        var breach = pivotType == "TOP"
            ? ordered.FirstOrDefault(item => item.HighPrice > pairPrice)
            : ordered.FirstOrDefault(item => item.LowPrice < pairPrice);
        decimal? extreme = ordered.Length == 0
            ? null
            : pivotType == "TOP"
                ? ordered.Max(static item => item.HighPrice)
                : ordered.Min(static item => item.LowPrice);
        var source = string.Join('|', ordered.Select(static item => item.SourceRowHash)
            .Concat(verifiedMissingEobs.Order().Select(static item => $"MISSING:{item:O}")));
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        var status = breach is not null ? "INVALIDATED" : ordered.Length == 0 ? "NO_TRADE" : "PASSED";
        return new PairTrendNextDayEvaluation(
            status,
            extreme,
            breach?.Eob,
            breach is null ? null : pivotType == "TOP" ? breach.HighPrice : breach.LowPrice,
            sourceHash,
            ordered.Length,
            verifiedMissingEobs.Count);
    }

    /// <summary>
    /// 盘中增量验证。调用方必须用已接收K线与三次确认的无成交EOB精确覆盖
    /// 当前水位之前的所有闭合窗口；未到15:00且未突破时返回 MONITORING。
    /// </summary>
    public static PairTrendNextDayEvaluation EvaluateRealtime(
        string pivotType,
        decimal pairPrice,
        IReadOnlyCollection<PairTrendNextDayBar> bars,
        IReadOnlyCollection<DateTime> verifiedMissingEobs,
        IReadOnlyCollection<DateTime> expectedClosedEobs,
        bool isFinal)
    {
        var expected = expectedClosedEobs.ToHashSet();
        var received = bars.Select(static item => item.Eob).ToHashSet();
        var missing = verifiedMissingEobs.ToHashSet();
        if (expected.Count != expectedClosedEobs.Count ||
            received.Count != bars.Count || missing.Count != verifiedMissingEobs.Count ||
            received.Overlaps(missing) || !received.Union(missing).ToHashSet().SetEquals(expected))
            throw new InvalidOperationException("盘中5分钟K线与无成交证明未精确覆盖当前闭合窗口。");

        var ordered = bars.OrderBy(static item => item.Eob).ToArray();
        pivotType = pivotType.Trim().ToUpperInvariant();
        if (pivotType is not ("TOP" or "BOTTOM"))
            throw new ArgumentException("pivotType 必须是 TOP 或 BOTTOM。", nameof(pivotType));
        if (pairPrice <= 0) throw new ArgumentOutOfRangeException(nameof(pairPrice));
        var breach = pivotType == "TOP"
            ? ordered.FirstOrDefault(item => item.HighPrice > pairPrice)
            : ordered.FirstOrDefault(item => item.LowPrice < pairPrice);
        decimal? extreme = ordered.Length == 0
            ? null
            : pivotType == "TOP"
                ? ordered.Max(static item => item.HighPrice)
                : ordered.Min(static item => item.LowPrice);
        var source = string.Join('|', ordered.Select(static item => item.SourceRowHash)
            .Concat(missing.Order().Select(static item => $"MISSING:{item:O}"))
            .Append($"FINAL:{isFinal}"));
        var sourceHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        var status = breach is not null
            ? "INVALIDATED"
            : !isFinal
                ? "MONITORING"
                : ordered.Length == 0 ? "NO_TRADE" : "PASSED";
        return new PairTrendNextDayEvaluation(
            status,
            extreme,
            breach?.Eob,
            breach is null ? null : pivotType == "TOP" ? breach.HighPrice : breach.LowPrice,
            sourceHash,
            ordered.Length,
            missing.Count);
    }
}
