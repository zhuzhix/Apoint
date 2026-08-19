using System.Text.Json;
using AStockMonitor.Application.Market;
using AStockMonitor.Domain.Market;
using StackExchange.Redis;

namespace AStockMonitor.Infrastructure.Market;

/// <summary>使用 Redis Lua 原子合并 Tick 为当日1分钟预览。</summary>
public sealed class RedisIntradayPreviewStore(
    RedisConnectionProvider connectionProvider,
    IntradayPreviewOptions options) : IIntradayPreviewStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string UpsertScript = """
        local current = redis.call('HGET', KEYS[1], ARGV[1])
        local event_ms = tonumber(ARGV[2])
        local price = tonumber(ARGV[3])
        local delta_volume = tonumber(ARGV[4])
        local delta_amount = tonumber(ARGV[5])
        local item
        if current then
            item = cjson.decode(current)
            if price > item.high then item.high = price end
            if price < item.low then item.low = price end
            if event_ms < item.firstTickTimeMs then
                item.firstTickTimeMs = event_ms
                item.open = price
            end
            if event_ms >= item.lastTickTimeMs then
                item.lastTickTimeMs = event_ms
                item.close = price
                item.rowHash = ARGV[6]
            end
            item.volume = item.volume + delta_volume
            item.amount = item.amount + delta_amount
            item.revision = item.revision + 1
            if item.sourceMode ~= ARGV[12] then item.sourceMode = 'MIXED' end
        else
            item = {
                symbol=ARGV[7], tradingDate=ARGV[8], bob=ARGV[9], eob=ARGV[10],
                open=price, high=price, low=price, close=price,
                volume=delta_volume, amount=delta_amount,
                firstTickTimeMs=event_ms, lastTickTimeMs=event_ms,
                revision=0, rowHash=ARGV[6], sourceMode=ARGV[12]
            }
        end
        local encoded = cjson.encode(item)
        redis.call('HSET', KEYS[1], ARGV[1], encoded)
        redis.call('EXPIRE', KEYS[1], tonumber(ARGV[11]))
        return encoded
        """;

    public async Task ProcessTickAsync(TickEvent tick, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tradingDate = ChinaMarketSession.TradingDate(tick.EventTime);
        var chinaTime = TimeZoneInfo.ConvertTime(tick.EventTime, ChinaMarketSession.TimeZone);
        var bob = new DateTimeOffset(
            chinaTime.Year, chinaTime.Month, chinaTime.Day, chinaTime.Hour, chinaTime.Minute, 0,
            chinaTime.Offset);
        var eob = bob.AddMinutes(1);
        var connection = await connectionProvider.GetAsync();
        var database = connection.GetDatabase();
        var delta = await CalculateDeltaAsync(database, tick, tradingDate);
        var key = Key(tick.Symbol, tradingDate);
        var field = bob.ToUnixTimeSeconds().ToString();
        var payload = await database.ScriptEvaluateAsync(
            UpsertScript,
            [(RedisKey)key],
            [
                field,
                tick.EventTime.ToUnixTimeMilliseconds(),
                tick.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),
                delta.Volume,
                delta.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                tick.EventId,
                tick.Symbol,
                tradingDate.ToString("yyyy-MM-dd"),
                bob.ToString("O"),
                eob.ToString("O"),
                Math.Max(3_600, options.TtlSeconds)
                ,tick.CollectionMode
            ]);
        await connection.GetSubscriber().PublishAsync(
            RedisChannel.Literal(options.UpdatedChannel), payload.ToString());
    }

    public async Task<IReadOnlyList<IntradayPreviewBar>> GetBarsAsync(
        string symbol,
        DateOnly tradingDate,
        DateTimeOffset observedAt,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = (await connectionProvider.GetAsync()).GetDatabase();
        var values = await database.HashValuesAsync(Key(symbol, tradingDate));
        return values
            .Where(static value => value.HasValue)
            .Select(value => JsonSerializer.Deserialize<PreviewPayload>(value.ToString(), JsonOptions))
            .Where(item => item is not null && item.Bob <= observedAt)
            .OrderBy(static item => item!.Bob)
            .TakeLast(Math.Clamp(limit, 1, 1_000))
            .Select(static item => item!.ToDomain())
            .ToArray();
    }

    private async Task<(long Volume, decimal Amount)> CalculateDeltaAsync(
        IDatabase database,
        TickEvent tick,
        DateOnly tradingDate)
    {
        var key = $"{options.KeyPrefix}:cumulative:{tradingDate:yyyy-MM-dd}:{tick.Symbol}";
        var old = await database.HashGetAsync(key, ["volume", "amount", "event_time_ms"]);
        var oldTime = old[2].TryParse(out long parsedTime) ? parsedTime : long.MinValue;
        if (tick.EventTime.ToUnixTimeMilliseconds() < oldTime)
            return (0, 0m);
        var oldVolume = old[0].TryParse(out long parsedVolume) ? parsedVolume : 0;
        var oldAmount = decimal.TryParse(old[1].ToString(), out var parsedAmount)
            ? parsedAmount : 0m;
        var usesTradeDelta = tick.LastVolume is not null || tick.LastAmount is not null;
        var volume = usesTradeDelta
            ? Math.Max(0, tick.LastVolume ?? 0)
            : tick.CumulativeVolume is null
                ? 0
                : Math.Max(0, tick.CumulativeVolume.Value - oldVolume);
        var amount = usesTradeDelta
            ? Math.Max(0m, tick.LastAmount ?? 0m)
            : tick.CumulativeAmount is null
                ? 0m
                : Math.Max(0m, tick.CumulativeAmount.Value - oldAmount);
        // Keep the cumulative baseline current even for subscribed Tick events.
        // When a symbol later leaves the hot pool, the first snapshot then starts
        // from the same watermark instead of recounting the subscribed interval.
        await database.HashSetAsync(key,
        [
            new HashEntry("volume", tick.CumulativeVolume ?? oldVolume),
            new HashEntry("amount", (tick.CumulativeAmount ?? oldAmount)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new HashEntry("event_time_ms", tick.EventTime.ToUnixTimeMilliseconds())
        ]);
        await database.KeyExpireAsync(key, TimeSpan.FromSeconds(Math.Max(3_600, options.TtlSeconds)));
        return (volume, amount);
    }

    private string Key(string symbol, DateOnly tradingDate) =>
        $"{options.KeyPrefix}:bars:{tradingDate:yyyy-MM-dd}:{symbol.Trim().ToUpperInvariant()}";

    private sealed class PreviewPayload
    {
        public string Symbol { get; init; } = string.Empty;
        public DateOnly TradingDate { get; init; }
        public DateTimeOffset Bob { get; init; }
        public DateTimeOffset Eob { get; init; }
        public decimal Open { get; init; }
        public decimal High { get; init; }
        public decimal Low { get; init; }
        public decimal Close { get; init; }
        public long Volume { get; init; }
        public decimal Amount { get; init; }
        public long FirstTickTimeMs { get; init; }
        public long LastTickTimeMs { get; init; }
        public int Revision { get; init; }
        public string RowHash { get; init; } = string.Empty;
        public string SourceMode { get; init; } = "UNKNOWN";

        public IntradayPreviewBar ToDomain() => new(
            Symbol, TradingDate, Bob, Eob, Open, High, Low, Close, Volume, Amount,
            DateTimeOffset.FromUnixTimeMilliseconds(FirstTickTimeMs),
            DateTimeOffset.FromUnixTimeMilliseconds(LastTickTimeMs), Revision, RowHash,
            SourceMode,
            SourceMode switch
            {
                "REALTIME_SUBSCRIPTION" => "realtime_tick",
                "SNAPSHOT_POLL" => "snapshot_derived",
                "MIXED" => "mixed",
                _ => "unknown"
            });
    }
}
