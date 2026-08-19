namespace AStockMonitor.Application.Market;

/// <summary>沪深 A 股连续竞价时段和 K 线切桶规则。</summary>
public static class ChinaMarketSession
{
    private static readonly TimeOnly MorningStart = new(9, 30);
    private static readonly TimeOnly MorningEnd = new(11, 30);
    private static readonly TimeOnly AfternoonStart = new(13, 0);
    private static readonly TimeOnly AfternoonEnd = new(15, 0);

    public static TimeZoneInfo TimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "China Standard Time" : "Asia/Shanghai");

    /// <summary>
    /// 将 Tick 归入左闭右开的交易桶；11:30 和 15:00 的收盘 Tick 归入前一根 K 线。
    /// 30/60 分钟桶分别在上午和下午重新起算，不跨越午休。
    /// </summary>
    public static bool TryGetBucket(
        DateTimeOffset eventTime,
        string frequency,
        out DateOnly tradingDate,
        out DateTimeOffset bob,
        out DateTimeOffset eob)
    {
        var local = TimeZoneInfo.ConvertTime(eventTime, TimeZone);
        tradingDate = DateOnly.FromDateTime(local.DateTime);
        var time = TimeOnly.FromDateTime(local.DateTime);

        if (frequency.Equals("1d", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsInSession(time))
            {
                bob = default;
                eob = default;
                return false;
            }

            bob = AtLocal(tradingDate, MorningStart);
            eob = AtLocal(tradingDate, AfternoonEnd);
            return true;
        }

        var minutes = frequency.ToLowerInvariant() switch
        {
            "1m" => 1,
            "5m" => 5,
            "30m" => 30,
            "60m" => 60,
            _ => 0
        };
        if (minutes == 0 || !TryGetSession(time, out var sessionStart, out var sessionEnd))
        {
            bob = default;
            eob = default;
            return false;
        }

        var elapsedMinutes = (time.ToTimeSpan() - sessionStart.ToTimeSpan()).TotalMinutes;
        var sessionMinutes = (sessionEnd.ToTimeSpan() - sessionStart.ToTimeSpan()).TotalMinutes;
        // 交易所收盘时间本身属于最后一个桶，避免额外生成零长度 K 线。
        var bucketIndex = elapsedMinutes >= sessionMinutes
            ? (int)Math.Ceiling(sessionMinutes / minutes) - 1
            : (int)Math.Floor(elapsedMinutes / minutes);
        var bucketStart = sessionStart.AddMinutes(bucketIndex * minutes);
        var bucketEnd = bucketStart.AddMinutes(minutes);
        if (bucketEnd > sessionEnd)
        {
            bucketEnd = sessionEnd;
        }

        bob = AtLocal(tradingDate, bucketStart);
        eob = AtLocal(tradingDate, bucketEnd);
        return true;
    }

    public static DateTimeOffset ToChinaTime(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, TimeZone);

    /// <summary>Returns the Asia/Shanghai calendar date used to isolate real-time Redis keys.</summary>
    public static DateOnly TradingDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(ToChinaTime(instant).DateTime);

    private static bool IsInSession(TimeOnly value) =>
        (value >= MorningStart && value <= MorningEnd) ||
        (value >= AfternoonStart && value <= AfternoonEnd);

    private static bool TryGetSession(TimeOnly value, out TimeOnly start, out TimeOnly end)
    {
        if (value >= MorningStart && value <= MorningEnd)
        {
            start = MorningStart;
            end = MorningEnd;
            return true;
        }

        if (value >= AfternoonStart && value <= AfternoonEnd)
        {
            start = AfternoonStart;
            end = AfternoonEnd;
            return true;
        }

        start = default;
        end = default;
        return false;
    }

    private static DateTimeOffset AtLocal(DateOnly date, TimeOnly time)
    {
        var unspecified = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZone.GetUtcOffset(unspecified));
    }
}
