namespace AStockMonitor.Domain.Market;

public sealed record TickEvent(
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
    int SourcePriority = 300);
