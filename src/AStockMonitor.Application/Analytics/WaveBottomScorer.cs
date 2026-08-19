using System.Security.Cryptography;
using System.Text;
using AStockMonitor.Domain.Analytics;

namespace AStockMonitor.Application.Analytics;

/// <summary>
/// Uses completed daily bars only. The result is a point-in-time supplement to a
/// BOTTOM event that has reached FOCUS; it never changes pair-trend-v3 state.
/// </summary>
public sealed class WaveBottomScorer(WaveBottomOptions options)
{
    private readonly WaveBottomOptions _options = Validate(options);

    public WaveBottomEvaluation Evaluate(IReadOnlyCollection<PairTrendBar> input)
    {
        var bars = input
            .Where(static bar => PairTrendV3Engine.NormalizeFrequency(bar.Frequency) == "1d")
            .OrderBy(static bar => bar.TradingDate)
            .ThenBy(static bar => bar.Eob)
            .GroupBy(static bar => DateOnly.FromDateTime(bar.TradingDate))
            .Select(static group => group.Last())
            .TakeLast(_options.RequiredDailyBars)
            .ToArray();
        var inputHash = Hash(string.Join('|', bars.Select(static bar =>
            $"{bar.Symbol}:{bar.TradingDate:yyyyMMdd}:{bar.SourceRowHash}")));

        if (bars.Length < _options.MinimumDailyBars)
        {
            return new WaveBottomEvaluation(
                "INSUFFICIENT_DATA", "NONE", 0, false,
                bars.LastOrDefault()?.Eob, bars.Length, _options.AlgorithmVersion,
                inputHash, []);
        }

        var closes = bars.Select(static bar => (double)bar.ClosePrice).ToArray();
        var highs = bars.Select(static bar => (double)bar.HighPrice).ToArray();
        var lows = bars.Select(static bar => (double)bar.LowPrice).ToArray();
        var volumes = bars.Select(static bar => (double)bar.Volume).ToArray();

        var return20 = closes[^1] / closes[^21] - 1d;
        var drawdown60 = closes[^1] / highs.TakeLast(60).Max() - 1d;
        var ma10 = closes.TakeLast(10).Average();
        var ma5 = closes.TakeLast(5).Average();
        var ma20 = closes.TakeLast(20).Average();
        var priorMa20 = closes.Skip(closes.Length - 25).Take(20).Average();
        var maDown = ma10 < ma20 && ma20 < priorMa20;
        var trendMatches = (return20 <= -0.08d ? 1 : 0) +
                           (drawdown60 <= -0.15d ? 1 : 0) +
                           (maDown ? 1 : 0);
        var trendGate = trendMatches >= 2;

        var rsi = Rsi(closes, 14);
        var rsiMatched = rsi.Length >= 5 && rsi.TakeLast(5).Min() <= 35d && rsi[^1] > rsi[^2];

        var histogram = MacdHistogram(closes);
        var macdMatched = histogram.Length >= 3 && histogram[^1] <= 0d &&
                          histogram[^1] > histogram[^2] && histogram[^2] > histogram[^3];

        var recentFiveLows = lows.TakeLast(5).ToArray();
        var noNewLow = recentFiveLows[^1] > recentFiveLows.Take(4).Min();
        var structure = HasBottomStructure(highs, lows);

        var priorMa10 = closes.Skip(closes.Length - 15).Take(10).Average();
        var maRecovered = closes[^1] > ma10 && ma10 >= priorMa10;
        var shortPressure = highs.Skip(highs.Length - 11).Take(10).Max();
        var shortPressureRecovered = closes[^1] > shortPressure && closes[^1] > ma5;
        var previousFiveVolume = volumes.Skip(volumes.Length - 6).Take(5).Average();
        var volumeImproved = previousFiveVolume > 0d && volumes[^1] >= previousFiveVolume * 1.2d;

        var components = new[]
        {
            Component("TREND_GATE", "趋势门禁（不计分）", 0, trendGate,
                $"满足={trendMatches}/3,20日收益={return20:P2},60日回撤={drawdown60:P2},均线下行={(maDown ? "是" : "否")}"),
            Component("RSI_TURN", "RSI超跌后上拐", 20, rsiMatched,
                rsi.Length == 0 ? "RSI不可用" : $"RSI14={rsi[^1]:F2}"),
            Component("MACD_CONTRACTION", "MACD空头动能收缩", 15, macdMatched,
                histogram.Length == 0 ? "MACD不可用" : $"柱线={histogram[^1]:F4}"),
            Component("NO_NEW_LOW", "价格停止创新低", 15, noNewLow,
                $"最近低点={recentFiveLows[^1]:F3}"),
            Component("BOTTOM_STRUCTURE", "底部结构", 15, structure,
                structure ? "更高低点或双底" : "未形成确定结构"),
            Component("MA_RECOVERY", "均线修复", 15, maRecovered,
                $"收盘={closes[^1]:F3},MA10={ma10:F3}"),
            Component("SHORT_PRESSURE_MA5", "突破短期压力并站上5日线", 10, shortPressureRecovered,
                $"收盘={closes[^1]:F3},前10日压力={shortPressure:F3},MA5={ma5:F3}"),
            Component("VOLUME_CONFIRM", "突破量能改善", 10, volumeImproved,
                previousFiveVolume <= 0d ? "均量不可用" : $"量比={volumes[^1] / previousFiveVolume:F2}")
        };
        var score = components.Where(static item => item.Matched).Sum(static item => item.Score);
        var signal = !trendGate || score < _options.CandidateThreshold
            ? "NONE"
            : score >= _options.StrongThreshold ? "STRONG" : "CANDIDATE";

        return new WaveBottomEvaluation(
            "COMPLETED", signal, score, trendGate, bars[^1].Eob, bars.Length,
            _options.AlgorithmVersion, inputHash, components);
    }

    private static WaveBottomComponent Component(
        string code, string label, int score, bool matched, string evidence) =>
        new(code, label, score, matched, evidence);

    private static bool HasBottomStructure(double[] highs, double[] lows)
    {
        var start = Math.Max(2, lows.Length - 45);
        var swings = new List<int>();
        for (var index = start; index < lows.Length - 2; index++)
        {
            if (lows[index] <= lows[index - 1] && lows[index] <= lows[index - 2] &&
                lows[index] <= lows[index + 1] && lows[index] <= lows[index + 2])
                swings.Add(index);
        }
        if (swings.Count < 2) return false;
        var previous = swings[^2];
        var current = swings[^1];
        var separation = current - previous;
        if (separation is < 5 or > 30) return false;
        var higherLow = lows[current] >= lows[previous] * 1.005d;
        var doubleBottom = Math.Abs(lows[current] / lows[previous] - 1d) <= 0.03d;
        var neckline = highs.Skip(previous).Take(current - previous + 1).Max();
        return higherLow || (doubleBottom && highs[^1] >= neckline);
    }

    private static double[] Rsi(double[] closes, int period)
    {
        if (closes.Length <= period) return [];
        var gains = 0d;
        var losses = 0d;
        for (var index = 1; index <= period; index++)
        {
            var change = closes[index] - closes[index - 1];
            gains += Math.Max(0d, change);
            losses += Math.Max(0d, -change);
        }
        var averageGain = gains / period;
        var averageLoss = losses / period;
        var result = new List<double> { RsiValue(averageGain, averageLoss) };
        for (var index = period + 1; index < closes.Length; index++)
        {
            var change = closes[index] - closes[index - 1];
            averageGain = (averageGain * (period - 1) + Math.Max(0d, change)) / period;
            averageLoss = (averageLoss * (period - 1) + Math.Max(0d, -change)) / period;
            result.Add(RsiValue(averageGain, averageLoss));
        }
        return result.ToArray();
    }

    private static double RsiValue(double gain, double loss) =>
        loss == 0d ? 100d : 100d - 100d / (1d + gain / loss);

    private static double[] MacdHistogram(double[] closes)
    {
        if (closes.Length < 35) return [];
        var ema12 = Ema(closes, 12);
        var ema26 = Ema(closes, 26);
        var dif = ema12.Zip(ema26, static (fast, slow) => fast - slow).ToArray();
        var signal = Ema(dif, 9);
        return dif.Zip(signal, static (value, dea) => (value - dea) * 2d).ToArray();
    }

    private static double[] Ema(double[] values, int period)
    {
        var result = new double[values.Length];
        result[0] = values[0];
        var multiplier = 2d / (period + 1d);
        for (var index = 1; index < values.Length; index++)
            result[index] = (values[index] - result[index - 1]) * multiplier + result[index - 1];
        return result;
    }

    private static string Hash(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static WaveBottomOptions Validate(WaveBottomOptions value)
    {
        value.Validate();
        return value;
    }
}
