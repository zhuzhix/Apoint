using System.Text.Json;
using AStockMonitor.Application.Market;
using AStockMonitor.Domain.Market;
using AStockMonitor.Infrastructure.Persistence;
using AStockMonitor.Infrastructure.Configuration;
using StackExchange.Redis;

namespace AStockMonitor.Infrastructure.Market;

internal sealed class LayeredMarketDataReader(
    IMarketStateStore memory,
    RedisConnectionProvider connectionProvider,
    MarketOptions options,
    MarketCollectionV4Options collectionOptions) : IMarketDataReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LatestQuote?> GetLatestAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        var inMemory = memory.Get(normalized);
        if (inMemory is not null && IsFresh(inMemory))
        {
            return inMemory;
        }

        try
        {
            var connection = await connectionProvider.GetAsync();
            var tradingDate = ChinaMarketSession.TradingDate(DateTimeOffset.UtcNow);
            var database = connection.GetDatabase();
            var payload = await database.HashGetAsync(
                options.GetV3TickLatestKey(
                    tradingDate, options.GetV3TickShard(normalized)),
                normalized);
            if (payload.HasValue)
            {
                var cachedTick = JsonSerializer.Deserialize<TickEvent>(
                    payload.ToString(), JsonOptions);
                if (cachedTick is not null && IsFresh(cachedTick))
                {
                    return LatestQuote.FromTick(cachedTick);
                }
            }

            if (!collectionOptions.Enabled)
            {
                // Temporary read compatibility before the V4 cutover only.
                payload = await database.HashGetAsync(
                    options.GetV2TickLatestKey(tradingDate, normalized), "payload");
                if (payload.HasValue)
                {
                    var legacy = JsonSerializer.Deserialize<LatestQuote>(payload.ToString(), JsonOptions);
                    if (legacy is not null && IsFresh(legacy))
                        return legacy;
                }
            }
        }
        catch
        {
            // V2 intentionally has no MySQL Tick fallback. A missing cache is
            // reported to the caller as unavailable real-time data.
        }

        return null;
    }

    public async Task<IReadOnlyDictionary<string, LatestQuote>> GetLatestBatchAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default)
    {
        var normalized = symbols
            .Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(static symbol => symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(1_000)
            .ToArray();
        var result = new Dictionary<string, LatestQuote>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var symbol in normalized)
        {
            var cached = memory.Get(symbol);
            if (cached is null)
                missing.Add(symbol);
            else if (IsFresh(cached))
                result[symbol] = cached;
            else
                missing.Add(symbol);
        }

        if (missing.Count == 0)
            return result;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var database = (await connectionProvider.GetAsync()).GetDatabase();
            var tradingDate = ChinaMarketSession.TradingDate(DateTimeOffset.UtcNow);
            var groups = missing.GroupBy(options.GetV3TickShard).ToArray();
            var tasks = groups.Select(async group =>
            {
                var fields = group.Select(static symbol => (RedisValue)symbol).ToArray();
                var values = await database.HashGetAsync(
                    options.GetV3TickLatestKey(tradingDate, group.Key), fields);
                return (Symbols: group.ToArray(), Values: values);
            }).ToArray();

            foreach (var groupResult in await Task.WhenAll(tasks))
            {
                for (var index = 0; index < groupResult.Symbols.Length; index++)
                {
                    if (!groupResult.Values[index].HasValue)
                        continue;
                    var tick = JsonSerializer.Deserialize<TickEvent>(
                        groupResult.Values[index].ToString(), JsonOptions);
                    if (tick is not null && IsFresh(tick))
                        result[groupResult.Symbols[index]] = LatestQuote.FromTick(tick);
                }
            }
        }
        catch
        {
            // Return the L0 subset. The controller exposes missing symbols so
            // callers never mistake a partial cache response for completeness.
        }

        return result;
    }

    private bool IsFresh(TickEvent tick) => IsFresh(
        tick.EventTime, tick.ReceiveTime, tick.CollectionMode);

    private bool IsFresh(LatestQuote quote) => IsFresh(
        quote.EventTime, quote.ReceiveTime, quote.CollectionMode);

    private bool IsFresh(
        DateTimeOffset eventTime,
        DateTimeOffset receiveTime,
        string collectionMode)
    {
        var now = DateTimeOffset.UtcNow;
        var maxAge = collectionMode.Equals("SNAPSHOT_POLL", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, collectionOptions.Snapshot.StaleQuoteSeconds)
            : Math.Max(1, options.TickMaxAgeSeconds);
        return eventTime >= now.AddSeconds(-maxAge) &&
               eventTime <= now.AddSeconds(30) &&
               receiveTime <= now.AddSeconds(30);
    }

    public async Task<IReadOnlyCollection<TickEvent>> GetRecentTicksAsync(
        string symbol,
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        var inMemory = memory.GetRecentTicks(normalized, since, limit);
        if (inMemory.Count > 0)
        {
            return inMemory;
        }

        // Recent Tick is deliberately an ephemeral L0 debugging/detail view.
        // Scanning a mixed shard Stream for one symbol creates unbounded read
        // amplification and competes with real-time consumers.
        cancellationToken.ThrowIfCancellationRequested();
        return [];
    }
}
