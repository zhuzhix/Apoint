using System.Collections.Concurrent;
using System.Threading.Channels;
using AStockMonitor.Domain.Market;

namespace AStockMonitor.Application.Market;

/// <summary>
/// In-process fan-out for low-latency projections. Each subscriber owns a
/// channel, so SignalR and future algorithms no longer compete for one Tick.
/// This bus is not the durable log; reliable consumers use the market data
/// service's replayable subscription boundary.
/// </summary>
public sealed class MarketEventBus
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    public MarketEventSubscription Subscribe(string name, int capacity = 20_000)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<TickEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // Live UI projections may coalesce under pressure. Redis Streams
            // remains the lossless path and is never affected by this choice.
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
        _subscribers[id] = new Subscriber(name, channel);
        return new MarketEventSubscription(channel.Reader, () => Remove(id));
    }

    public void Publish(TickEvent tick)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Channel.Writer.TryWrite(tick);
        }
    }

    private void Remove(Guid id)
    {
        if (_subscribers.TryRemove(id, out var subscriber))
        {
            subscriber.Channel.Writer.TryComplete();
        }
    }

    private sealed record Subscriber(string Name, Channel<TickEvent> Channel);
}

public sealed class MarketEventSubscription(
    ChannelReader<TickEvent> reader,
    Action dispose) : IAsyncDisposable
{
    public ChannelReader<TickEvent> Reader { get; } = reader;

    public ValueTask DisposeAsync()
    {
        dispose();
        return ValueTask.CompletedTask;
    }
}
