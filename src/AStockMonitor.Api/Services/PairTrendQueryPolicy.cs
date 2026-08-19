using AStockMonitor.Api.Models;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Application.Market;

namespace AStockMonitor.Api.Services;

public static class PairTrendQueryPolicy
{
    public const string LiveStockGroupsRoute = "live/stock-groups";
    public const string LiveStockGroupEventsRoute = "live/stock-groups/{symbol}/events";

    public static PairTrendDateRange ResolveRange(
        DateOnly? from,
        DateOnly? to,
        DateOnly today,
        PairTrendQueryOptions options)
    {
        var resolvedTo = to ?? today;
        var resolvedFrom = from ?? resolvedTo.AddDays(-59);
        if (resolvedFrom > resolvedTo)
            throw new ArgumentException("dateFrom must be on or before dateTo.");
        if (resolvedTo.DayNumber - resolvedFrom.DayNumber + 1 > options.MaximumDateRangeDays)
            throw new ArgumentException(
                $"Date range cannot exceed {options.MaximumDateRangeDays} inclusive days.");
        return new PairTrendDateRange(
            resolvedFrom,
            resolvedTo,
            resolvedFrom.ToDateTime(TimeOnly.MinValue),
            resolvedTo.AddDays(1).ToDateTime(TimeOnly.MinValue));
    }

    public static (int Page, int PageSize) NormalizePage(int page, int pageSize, int defaultSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize <= 0 ? defaultSize : pageSize, 1, 200));

    public static long CalculateOffset(int page, int pageSize) =>
        checked((long)(Math.Max(1, page) - 1) * pageSize);

    public static long CalculateTotalPages(long total, int pageSize) =>
        total == 0 ? 0 : (long)Math.Ceiling((decimal)total / pageSize);

    public static string ResolveStageAtEnd(
        DateTime statusAtExclusive,
        DateTime? observedAt,
        DateTime? focusedAt,
        DateTime? establishedAt,
        DateTime? invalidatedAt)
    {
        if (invalidatedAt is not null && invalidatedAt < statusAtExclusive) return "INVALIDATED";
        if (establishedAt is not null && establishedAt < statusAtExclusive) return "ESTABLISHED";
        if (focusedAt is not null && focusedAt < statusAtExclusive) return "FOCUS";
        if (observedAt is not null && observedAt < statusAtExclusive) return "OBSERVING";
        return "DISCOVERED";
    }

    public static string ResolveMarketDayStatus(PairTrendMarketDayRow? row) =>
        row is null || !row.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            ? "CALENDAR_PENDING"
            : row.IsTradingDay ? "TRADING_DAY" : "NON_TRADING_DAY";

    public static string ResolveSessionStatus(DateTimeOffset now, string marketDayStatus)
    {
        if (marketDayStatus != "TRADING_DAY")
            return "UNAVAILABLE";
        var local = ChinaMarketSession.ToChinaTime(now);
        var time = TimeOnly.FromDateTime(local.DateTime);
        if (time < new TimeOnly(9, 30)) return "PRE_OPEN";
        if (time <= new TimeOnly(11, 30)) return "MORNING_SESSION";
        if (time < new TimeOnly(13, 0)) return "MIDDAY_BREAK";
        if (time <= new TimeOnly(15, 0)) return "AFTERNOON_SESSION";
        return "CLOSED";
    }
}
