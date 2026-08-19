using System.Collections.Concurrent;
using AStockMonitor.Domain.Market;

namespace AStockMonitor.Application.Market;

public sealed class MarketEventProcessor(IMarketStateStore stateStore, MarketEventBus eventBus)
{
    private readonly ConcurrentDictionary<string, byte> _recentEventIds = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _recentEventOrder = new();
    private const int MaxRecentEventIds = 100_000;

    public bool TryProcess(TickEvent tick)
    {
        if (string.IsNullOrWhiteSpace(tick.EventId) || string.IsNullOrWhiteSpace(tick.Symbol))
        {
            return false;
        }

        if (!_recentEventIds.TryAdd(tick.EventId, 0))
        {
            return false;
        }

        _recentEventOrder.Enqueue(tick.EventId);
        while (_recentEventIds.Count > MaxRecentEventIds && _recentEventOrder.TryDequeue(out var expired))
        {
            _recentEventIds.TryRemove(expired, out _);
        }

        stateStore.Upsert(tick);
        eventBus.Publish(tick);
        return true;
    }
}
