using System.Security.Cryptography;
using System.Text;
using AStockMonitor.Domain.Analytics;

namespace AStockMonitor.Application.Analytics;

/// <summary>
/// V3 单周期对子识别器。它只识别 High/Low 对子事实，不再使用 EMA、ATR、趋势或反转阈值。
/// 跨周期升级和后续突破由 <see cref="PairTrendV3Engine"/> 统一处理。
/// </summary>
public sealed class PairTrendAnalyzer(PairTrendOptions options)
{
    private readonly PairTrendOptions _options = Validate(options);

    public IReadOnlyList<PairTrendHitResult> AnalyzeFrequency(
        IReadOnlyList<PairTrendBar> sourceBars,
        DateOnly dateFrom,
        DateOnly dateTo)
    {
        var hits = new List<PairTrendHitResult>();
        foreach (var bar in sourceBars.OrderBy(static item => item.Eob))
        {
            var date = DateOnly.FromDateTime(bar.TradingDate);
            if (date < dateFrom || date > dateTo)
                continue;
            Add(bar, PairPivotType.Top, bar.HighPrice, hits);
            Add(bar, PairPivotType.Bottom, bar.LowPrice, hits);
        }
        return hits;
    }

    private void Add(
        PairTrendBar bar,
        PairPivotType pivot,
        decimal price,
        ICollection<PairTrendHitResult> hits)
    {
        var match = PairPriceMatcher.Match(price, _options.PriceTick, _options.IncludeRound00);
        if (match is null)
            return;
        var frequency = NormalizeFrequency(bar.Frequency);
        var key = Hash(string.Join('|', bar.Symbol, frequency, bar.Eob.ToString("O"),
            pivot, match.PriceTicks, _options.AlgorithmVersion));
        hits.Add(new PairTrendHitResult(
            key, bar.Symbol, frequency, bar.TradingDate, bar.Bob, bar.Eob, bar.Eob, null,
            pivot, PairHitStatus.Candidate, match.Price, match.PriceTicks, match.PairCode,
            match.Kind, pivot == PairPivotType.Top ? "HIGH" : "LOW",
            PairTrendDirection.Unknown, 0m, 0m, 0m, 0m, bar.PreClose,
            bar.OpenPrice, bar.HighPrice, bar.LowPrice, bar.ClosePrice,
            Math.Max(0, bar.Volume), Math.Max(0, bar.Amount), false, 0m, 0m, 0m,
            0.25m, "PAIR_PRICE_DETECTED", bar.SourceRowHash,
            _options.AlgorithmVersion));
    }

    public static string NormalizeFrequency(string frequency) => frequency.Trim().ToLowerInvariant() switch
    {
        "300s" => "5m", "1800s" => "30m", "3600s" => "60m", "day" => "1d",
        var value => value
    };

    internal static string Hash(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static PairTrendOptions Validate(PairTrendOptions value)
    {
        value.Validate();
        return value;
    }
}
