namespace AStockMonitor.Application.Analytics;

/// <summary>Controls the read-only pair-trend query surfaces.</summary>
public sealed class PairTrendQueryOptions
{
    public bool HistoricalDataEnabled { get; set; } = true;
    public bool IntradayEnabled { get; set; } = true;
    public bool HistoricalReplayEnabled { get; set; }
    public int IntradayRefreshSeconds { get; set; } = 30;
    public int MaximumDateRangeDays { get; set; } = 366;
    public int HistoricalGroupCacheSeconds { get; set; } = 60;
    public int IntradayGroupCacheSeconds { get; set; } = 10;
    public bool UseQueryProjection { get; set; } = true;

    public void Validate()
    {
        if (IntradayRefreshSeconds is < 5 or > 300)
            throw new ArgumentOutOfRangeException(nameof(IntradayRefreshSeconds));
        if (MaximumDateRangeDays is < 1 or > 3660)
            throw new ArgumentOutOfRangeException(nameof(MaximumDateRangeDays));
        if (HistoricalGroupCacheSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(HistoricalGroupCacheSeconds));
        if (IntradayGroupCacheSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(IntradayGroupCacheSeconds));
    }
}
