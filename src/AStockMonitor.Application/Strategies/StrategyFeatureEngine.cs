using AStockMonitor.Domain.Strategies;

namespace AStockMonitor.Application.Strategies;

/// <summary>将原始行情窗口转换为所有策略共用的确定性技术特征。</summary>
public sealed class StrategyFeatureEngine : IStrategyFeatureEngine
{
    public StrategySnapshot Build(StrategySnapshotInput input)
    {
        var minute = input.Minute1Bars.OrderBy(static x => x.Eob).ToArray();
        var daily = input.DailyBars.OrderBy(static x => x.Eob).ToArray();
        var bars30 = input.Minute30Bars.OrderBy(static x => x.Eob).ToArray();
        var weekly = AggregateCompletedWeeks(daily, input.ObservedAt).ToArray();
        var quote = input.Quote;

        var price = quote?.Price ?? minute.LastOrDefault()?.Close ?? daily.LastOrDefault()?.Close ?? 0m;
        var preClose = quote?.PreClose ?? daily.LastOrDefault()?.Close ?? 0m;
        var dayOpen = minute.FirstOrDefault()?.Open ?? price;
        var dayHigh = minute.Length == 0 ? price : Math.Max(price, minute.Max(static x => x.High));
        var dayLow = minute.Length == 0 ? price : Math.Min(price, minute.Min(static x => x.Low));
        var amount = quote?.CumulativeAmount ?? minute.Sum(static x => x.Amount);
        var volume = quote?.CumulativeVolume ?? minute.Sum(static x => x.Volume);
        var vwap = volume > 0 && amount > 0 ? amount / volume : price;
        var range = dayHigh - dayLow;

        var closesAbove = ConsecutiveClosesAboveCumulativeVwap(minute);
        var barsPerFiveMinutes = BarsForMinutes(5, input.IntradayBarMinutes);
        var barsPerFifteenMinutes = BarsForMinutes(15, input.IntradayBarMinutes);
        var platform15 = minute.Length > 1
            ? minute.Skip(Math.Max(0, minute.Length - barsPerFifteenMinutes - 1))
                .Take(Math.Min(barsPerFifteenMinutes, minute.Length - 1)).Max(static x => x.High)
            : price;
        var ma5 = AverageClose(daily, 5);
        var ma10 = AverageClose(daily, 10);
        var ma20 = AverageClose(daily, 20);
        var ma30 = AverageClose(daily, 30);
        var ma60 = AverageClose(daily, 60);
        var priorMa20 = daily.Length >= 21
            ? daily.Skip(daily.Length - 21).Take(20).Average(static x => x.Close)
            : ma20;
        var support = new[] { ma20, ma30 }.Where(static x => x > 0).DefaultIfEmpty(0).Min();
        var avgDailyVolume = daily.TakeLast(Math.Min(5, daily.Length)).Select(static x => (decimal)x.Volume).DefaultIfEmpty(0).Average();
        var elapsed = TradingMinutesElapsed(input.ObservedAt);
        var volumeRatio = avgDailyVolume > 0 && elapsed > 0
            ? volume / (avgDailyVolume * elapsed / 240m)
            : 0m;
        var latest30 = bars30.LastOrDefault(static x => x.IsClosed);
        var previous30 = bars30.Where(static x => x.IsClosed).TakeLast(6).SkipLast(1).ToArray();
        var weeklyMetrics = BuildWeeklyMetrics(weekly, price);
        var wave = BuildDeclineWaveFeatures(daily, price);

        var features = new StrategyFeatures
        {
            Price = price,
            PreClose = preClose,
            DayOpen = dayOpen,
            DayHigh = dayHigh,
            DayLow = dayLow,
            ChangePercent = Percent(price, preClose),
            FromOpenPercent = Percent(price, dayOpen),
            PullbackFromHighPercent = dayHigh > 0 ? (dayHigh - price) / dayHigh * 100m : 0m,
            IntradayClosePosition = range > 0 ? (price - dayLow) / range : 1m,
            UpperWickRatio = range > 0 ? (dayHigh - Math.Max(dayOpen, price)) / range : 0m,
            Vwap = vwap,
            AboveVwapPercent = Percent(price, vwap),
            Amount = amount,
            Volume = volume,
            VolumeRatio = volumeRatio,
            Last5MinuteReturnPercent = minute.Length > barsPerFiveMinutes
                ? Percent(price, minute[^(barsPerFiveMinutes + 1)].Close) : 0m,
            VolumeAcceleration5 = WindowVolumeRatio(minute, barsPerFiveMinutes),
            Recent3VolumeRatio = WindowVolumeRatio(minute, BarsForMinutes(3, input.IntradayBarMinutes)),
            Recent3VolumeNonDecreasing = IsNonDecreasing(minute.TakeLast(3).Select(static x => x.Volume)),
            ConsecutiveClosesAboveVwap = closesAbove,
            VwapPullbackRestart = DetectVwapPullbackRestart(minute),
            MinutePlatformHigh15 = platform15,
            MinutePlatformBreakoutPercent = Percent(price, platform15),
            Ma5 = ma5,
            Ma10 = ma10,
            Ma20 = ma20,
            Ma30 = ma30,
            Ma60 = ma60,
            Ma20SlopePercent = Percent(ma20, priorMa20),
            TrendStrengthPercent = Percent(ma20, ma60),
            FiveDayReturnPercent = daily.Length >= 5 ? Percent(price, daily[^5].Close) : 0m,
            PullbackVolumeRatio = PullbackVolumeRatio(daily),
            SupportPrice = support,
            DistanceFromSupportPercent = Percent(price, support),
            MarketAverageChangePercent = input.MarketAverageChangePercent ?? 0m,
            RelativeMarketStrengthPercent = Percent(price, preClose) - (input.MarketAverageChangePercent ?? 0m),
            WeekPlatformHigh24 = weeklyMetrics.High24,
            WeekPlatformLow24 = weeklyMetrics.Low24,
            WeekRange24Percent = weeklyMetrics.Range24,
            WeekRange12Percent = weeklyMetrics.Range12,
            WeekPlatformTouches = weeklyMetrics.Touches,
            Latest30VolumeRatio = latest30 is not null && previous30.Length > 0 && previous30.Average(static x => x.Volume) > 0
                ? latest30.Volume / (decimal)previous30.Average(static x => x.Volume) : 0m,
            Latest30ClosePosition = latest30 is null ? 0m : ClosePosition(latest30),
            Latest30UpperWickRatio = latest30 is null ? 0m : UpperWick(latest30),
            Latest30Bullish = latest30 is not null && latest30.Close > latest30.Open,
            DeclineFromMajorHighPercent = wave.Decline,
            ReboundFromPrimaryLowPercent = wave.Rebound,
            RepairFromSecondaryLowPercent = wave.Repair,
            HasSecondaryBottom = wave.HasSecondaryBottom,
            BreaksDescendingTrend = wave.BreaksTrend
        };

        return new StrategySnapshot(input, weekly, features);
    }

    private static IEnumerable<StrategyBar> AggregateCompletedWeeks(
        IReadOnlyList<StrategyBar> daily,
        DateTimeOffset observedAt)
    {
        var currentWeek = StartOfWeek(observedAt.Date);
        return daily
            .Where(x => StartOfWeek(x.Eob.Date) < currentWeek)
            .GroupBy(x => StartOfWeek(x.Eob.Date))
            .OrderBy(static x => x.Key)
            .Select(group =>
            {
                var bars = group.OrderBy(static x => x.Eob).ToArray();
                return new StrategyBar(
                    "1w", bars[0].Bob, bars[^1].Eob, bars[0].Open,
                    bars.Max(static x => x.High), bars.Min(static x => x.Low), bars[^1].Close,
                    bars.Sum(static x => x.Volume), bars.Sum(static x => x.Amount), true,
                    bars.Max(static x => x.Revision), string.Join(':', bars.Select(static x => x.RowHash)));
            });
    }

    private static DateTime StartOfWeek(DateTime date) =>
        date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static decimal AverageClose(IReadOnlyList<StrategyBar> bars, int count) =>
        bars.Count < count ? 0m : bars.TakeLast(count).Average(static x => x.Close);

    private static decimal Percent(decimal value, decimal baseline) =>
        baseline == 0 ? 0 : (value - baseline) / baseline * 100m;

    private static decimal WindowVolumeRatio(IReadOnlyList<StrategyBar> bars, int size)
    {
        if (bars.Count < size * 2) return 0m;
        var latest = bars.TakeLast(size).Average(static x => (decimal)x.Volume);
        var previous = bars.Skip(bars.Count - size * 2).Take(size).Average(static x => (decimal)x.Volume);
        return previous <= 0 ? 0m : latest / previous;
    }

    private static int BarsForMinutes(int minutes, int barMinutes) =>
        Math.Max(1, (int)Math.Ceiling(minutes / (double)Math.Max(1, barMinutes)));

    private static bool IsNonDecreasing(IEnumerable<long> source)
    {
        var values = source.ToArray();
        return values.Length >= 3 && values[0] <= values[1] && values[1] <= values[2];
    }

    private static int ConsecutiveClosesAboveCumulativeVwap(IReadOnlyList<StrategyBar> bars)
    {
        if (bars.Count == 0) return 0;
        decimal amount = 0;
        long volume = 0;
        var flags = new bool[bars.Count];
        for (var i = 0; i < bars.Count; i++)
        {
            amount += bars[i].Amount;
            volume += bars[i].Volume;
            flags[i] = volume > 0 && bars[i].Close >= amount / volume;
        }
        var count = 0;
        for (var i = flags.Length - 1; i >= 0 && flags[i]; i--) count++;
        return count;
    }

    private static bool DetectVwapPullbackRestart(IReadOnlyList<StrategyBar> bars)
    {
        var recent = bars.TakeLast(8).ToArray();
        if (recent.Length < 4) return false;
        decimal amount = 0;
        long volume = 0;
        var all = bars.ToArray();
        var cumulative = new Dictionary<DateTimeOffset, decimal>();
        foreach (var bar in all)
        {
            amount += bar.Amount;
            volume += bar.Volume;
            cumulative[bar.Eob] = volume > 0 ? amount / volume : bar.Close;
        }
        for (var i = 0; i <= recent.Length - 3; i++)
        {
            var pullback = recent[i];
            var vwap = cumulative[pullback.Eob];
            if (pullback.Low >= vwap * 0.997m && pullback.Close >= vwap &&
                recent[^2].Close >= cumulative[recent[^2].Eob] &&
                recent[^1].Close >= cumulative[recent[^1].Eob] &&
                Percent(recent[^1].Close, pullback.Close) >= 0.20m)
                return true;
        }
        return false;
    }

    private static decimal PullbackVolumeRatio(IReadOnlyList<StrategyBar> daily)
    {
        if (daily.Count < 15) return 0m;
        var recent = daily.TakeLast(5).Average(static x => (decimal)x.Volume);
        var prior = daily.Skip(daily.Count - 15).Take(10).Average(static x => (decimal)x.Volume);
        return prior <= 0 ? 0m : recent / prior;
    }

    private static decimal TradingMinutesElapsed(DateTimeOffset time)
    {
        var local = TimeZoneInfo.ConvertTime(time, ChinaTimeZone()).TimeOfDay;
        if (local < new TimeSpan(9, 30, 0)) return 0;
        if (local <= new TimeSpan(11, 30, 0)) return (decimal)(local - new TimeSpan(9, 30, 0)).TotalMinutes;
        if (local < new TimeSpan(13, 0, 0)) return 120;
        if (local <= new TimeSpan(15, 0, 0)) return 120 + (decimal)(local - new TimeSpan(13, 0, 0)).TotalMinutes;
        return 240;
    }

    private static TimeZoneInfo ChinaTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "China Standard Time" : "Asia/Shanghai");

    private static decimal ClosePosition(StrategyBar bar) =>
        bar.High == bar.Low ? 1m : (bar.Close - bar.Low) / (bar.High - bar.Low);

    private static decimal UpperWick(StrategyBar bar) =>
        bar.High == bar.Low ? 0m : (bar.High - Math.Max(bar.Open, bar.Close)) / (bar.High - bar.Low);

    private static (decimal High24, decimal Low24, decimal Range24, decimal Range12, int Touches)
        BuildWeeklyMetrics(IReadOnlyList<StrategyBar> weekly, decimal price)
    {
        var last24 = weekly.TakeLast(24).ToArray();
        var last12 = weekly.TakeLast(12).ToArray();
        if (last24.Length < 12) return (0, 0, 0, 0, 0);
        var high = last24.Max(static x => x.High);
        var low = last24.Min(static x => x.Low);
        var high12 = last12.Max(static x => x.High);
        var low12 = last12.Min(static x => x.Low);
        var touches = last24.Count(x => high > 0 && Math.Abs(x.High - high) / high <= 0.03m);
        return (high, low, Percent(high, low), Percent(high12, low12), touches);
    }

    private static (decimal Decline, decimal Rebound, decimal Repair, bool HasSecondaryBottom, bool BreaksTrend)
        BuildDeclineWaveFeatures(IReadOnlyList<StrategyBar> daily, decimal price)
    {
        var bars = daily.TakeLast(140).ToArray();
        if (bars.Length < 120) return (0, 0, 0, false, false);
        var highIndex = Array.FindIndex(bars, x => x.High == bars.Max(static b => b.High));
        if (highIndex < 0 || highIndex >= bars.Length - 20) return (0, 0, 0, false, false);
        var afterHigh = bars.Skip(highIndex + 1).ToArray();
        var low1 = afterHigh.Min(static x => x.Low);
        var low1Index = highIndex + 1 + Array.FindIndex(afterHigh, x => x.Low == low1);
        if (low1Index >= bars.Length - 5) return (Percent(low1, bars[highIndex].High), 0, 0, false, false);
        var reboundBars = bars.Skip(low1Index + 1).ToArray();
        var reboundHigh = reboundBars.Max(static x => x.High);
        var reboundHighIndex = low1Index + 1 + Array.FindIndex(reboundBars, x => x.High == reboundHigh);
        var secondary = bars.Skip(Math.Min(reboundHighIndex + 1, bars.Length - 1)).ToArray();
        var low2 = secondary.Min(static x => x.Low);
        var hasSecond = secondary.Length >= 2 && low2 >= low1 * 0.98m && low2 <= reboundHigh * 0.97m;
        var ma10 = bars.TakeLast(10).Average(static x => x.Close);
        return (
            bars[highIndex].High > 0 ? (bars[highIndex].High - low1) / bars[highIndex].High * 100m : 0,
            Percent(reboundHigh, low1),
            Percent(price, low2),
            hasSecond,
            price > Math.Max(ma10, bars.TakeLast(20).Average(static x => x.Close)));
    }
}
