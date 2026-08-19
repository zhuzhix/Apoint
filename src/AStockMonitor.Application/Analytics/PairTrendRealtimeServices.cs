using AStockMonitor.Contracts.Market;
using AStockMonitor.Domain.Market;

namespace AStockMonitor.Application.Analytics;

/// <summary>盘中对子顶底可靠消费参数。</summary>
public sealed class PairTrendRealtimeOptions
{
    public bool Enabled { get; set; } = true;
    public string ConsumerGroup { get; set; } = "pair-trend-realtime-v3";
    public int EventBatchSize { get; set; } = 100;
    public int PendingIdleMilliseconds { get; set; } = 60_000;
    public int PendingRecoveryIntervalSeconds { get; set; } = 30;
    public string EventStream { get; set; } = "pair:v3:event";
    public int OutboxLeaseSeconds { get; set; } = 30;
    public int OutboxMaxAttempts { get; set; } = 10;
    public bool TickInvalidationEnabled { get; set; } = true;
    public string TickConsumerGroup { get; set; } = "pair-trend-tick-v3";
    public int TickBatchSize { get; set; } = 500;
}

/// <summary>完成单条官方 K 线事件的实时对子计算和事务落库。</summary>
public interface IPairTrendRealtimeService
{
    Task ProcessAsync(
        BarLifecycleEventV2 barEvent,
        int shard,
        string streamMessageId,
        CancellationToken cancellationToken);
}

/// <summary>缓存中供 Tick 快速判断的活动对子价位。</summary>
public sealed record PairTrendActiveLevel(
    long EventId,
    string EventKey,
    string Symbol,
    string? SymbolName,
    string PivotType,
    string Stage,
    decimal PairPrice,
    long PriceTicks,
    byte PairCode,
    string PairKind,
    uint Generation,
    uint EventRevision);

/// <summary>进程内活动价位索引；Tick 不命中突破条件时禁止访问 MySQL。</summary>
public interface IPairTrendActiveLevelCache
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task ReloadSymbolAsync(string symbol, CancellationToken cancellationToken);
    IReadOnlyList<PairTrendActiveLevel> Get(string symbol);
}

/// <summary>把已经通过内存预筛的 Tick 突破事务化为对子失效和可靠消息。</summary>
public interface IPairTrendTickInvalidationService
{
    Task InvalidateAsync(
        TickEvent tick,
        IReadOnlyCollection<long> candidateEventIds,
        CancellationToken cancellationToken);
}
