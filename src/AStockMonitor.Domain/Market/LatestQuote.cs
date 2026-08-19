namespace AStockMonitor.Domain.Market;

/// <summary>API 内存中单只股票的最新行情快照。</summary>
public sealed record LatestQuote(
    string EventId,
    string Symbol,
    DateTimeOffset EventTime,
    DateTimeOffset ReceiveTime,
    decimal Price,
    decimal? PreClose,
    long? CumulativeVolume,
    decimal? CumulativeAmount,
    long? LastVolume,
    decimal? LastAmount,
    decimal? BidPrice1,
    long? BidVolume1,
    decimal? AskPrice1,
    long? AskVolume1,
    string Source,
    string? WorkerId,
    string? SessionId = null,
    long WorkerSequence = 0,
    DateTimeOffset? ServerReceiveTime = null,
    string CollectionMode = "REALTIME_SUBSCRIPTION",
    int SourcePriority = 300)
{
    /// <summary>相对前收价的涨跌幅百分比；前收缺失或无效时为空。</summary>
    public decimal? ChangePercent => PreClose is > 0
        ? (Price - PreClose.Value) / PreClose.Value * 100m
        : null;

    public static LatestQuote FromTick(TickEvent tick) => new(
        tick.EventId,
        tick.Symbol,
        tick.EventTime,
        tick.ReceiveTime,
        tick.Price,
        tick.PreClose,
        tick.CumulativeVolume,
        tick.CumulativeAmount,
        tick.LastVolume,
        tick.LastAmount,
        tick.BidPrice1,
        tick.BidVolume1,
        tick.AskPrice1,
        tick.AskVolume1,
        tick.Source,
        tick.WorkerId,
        tick.SessionId,
        tick.WorkerSequence,
        tick.ServerReceiveTime,
        tick.CollectionMode,
        tick.SourcePriority);
}
