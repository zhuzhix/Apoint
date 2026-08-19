using AStockMonitor.Domain.Market;

namespace AStockMonitor.Application.Market;

/// <summary>Redis 当日1分钟预览参数；预览永不写入 MySQL 正式 K 线表。</summary>
public sealed class IntradayPreviewOptions
{
    public bool Enabled { get; set; } = true;
    public string ConsumerGroup { get; set; } = "intraday-preview-v2";
    public string KeyPrefix { get; set; } = "md:v2:preview:1m";
    public string UpdatedChannel { get; set; } = "md:v2:preview:1m:updated";
    public int TtlSeconds { get; set; } = 259_200;
    public int EventBatchSize { get; set; } = 500;
    public int PendingIdleMilliseconds { get; set; } = 60_000;
}

/// <summary>可覆盖更新的当日1分钟预览。</summary>
public sealed record IntradayPreviewBar(
    string Symbol,
    DateOnly TradingDate,
    DateTimeOffset Bob,
    DateTimeOffset Eob,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    decimal Amount,
    DateTimeOffset FirstTickTime,
    DateTimeOffset LastTickTime,
    int Revision,
    string RowHash,
    string SourceMode = "UNKNOWN",
    string Quality = "unknown");

public interface IIntradayPreviewStore
{
    Task ProcessTickAsync(TickEvent tick, CancellationToken cancellationToken);

    Task<IReadOnlyList<IntradayPreviewBar>> GetBarsAsync(
        string symbol,
        DateOnly tradingDate,
        DateTimeOffset observedAt,
        int limit,
        CancellationToken cancellationToken);
}
