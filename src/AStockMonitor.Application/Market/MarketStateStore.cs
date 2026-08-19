using System.Collections.Concurrent;
using AStockMonitor.Domain.Market;

namespace AStockMonitor.Application.Market;

public interface IMarketStateStore
{
    LatestQuote? Get(string symbol);
    IReadOnlyCollection<LatestQuote> GetAll();
    IReadOnlyCollection<TickEvent> GetRecentTicks(string symbol, DateTimeOffset since, int limit);
    void Upsert(TickEvent tick);
}

public sealed class InMemoryMarketStateStore(MarketMemoryOptions options) : IMarketStateStore
{
    private readonly ConcurrentDictionary<string, LatestQuote> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TickRingBuffer> _recent = new(StringComparer.OrdinalIgnoreCase);

    public LatestQuote? Get(string symbol) =>
        _latest.TryGetValue(symbol, out var quote) ? quote : null;

    public IReadOnlyCollection<LatestQuote> GetAll() => _latest.Values.ToArray();

    public IReadOnlyCollection<TickEvent> GetRecentTicks(string symbol, DateTimeOffset since, int limit)
    {
        if (!_recent.TryGetValue(symbol, out var buffer))
        {
            return [];
        }

        return buffer.Read(since, Math.Clamp(limit, 1, 10_000));
    }

    public void Upsert(TickEvent tick)
    {
        var quote = LatestQuote.FromTick(tick);

        _latest.AddOrUpdate(
            tick.Symbol,
            quote,
            (_, existing) => IsNewer(quote, existing) ? quote : existing);

        if (options.RecentTicksPerSymbol > 0)
        {
            _recent.GetOrAdd(
                    tick.Symbol,
                    _ => new TickRingBuffer(options.RecentTicksPerSymbol))
                .Append(tick);
        }
    }

    private static bool IsNewer(LatestQuote incoming, LatestQuote existing) =>
        incoming.EventTime > existing.EventTime ||
        (incoming.EventTime == existing.EventTime && incoming.WorkerSequence >= existing.WorkerSequence);

    private sealed class TickRingBuffer(int capacity)
    {
        private readonly object _sync = new();
        private readonly TickEvent?[] _items = new TickEvent[Math.Max(1, capacity)];
        private int _next;
        private int _count;

        public void Append(TickEvent tick)
        {
            lock (_sync)
            {
                _items[_next] = tick;
                _next = (_next + 1) % _items.Length;
                _count = Math.Min(_count + 1, _items.Length);
            }
        }

        public IReadOnlyCollection<TickEvent> Read(DateTimeOffset since, int limit)
        {
            lock (_sync)
            {
                var result = new List<TickEvent>(Math.Min(_count, limit));
                for (var offset = 0; offset < _count && result.Count < limit; offset++)
                {
                    var index = (_next - 1 - offset + _items.Length) % _items.Length;
                    var tick = _items[index];
                    if (tick is null)
                    {
                        continue;
                    }

                    if (tick.EventTime < since)
                    {
                        // Arrival order can differ from source event order, so
                        // an old Tick does not prove that all earlier slots are old.
                        continue;
                    }

                    result.Add(tick);
                }

                result.Reverse();
                return result;
            }
        }
    }
}
