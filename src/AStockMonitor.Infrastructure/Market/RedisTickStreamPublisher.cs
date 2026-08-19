using System.Globalization;
using System.Text.Json;
using AStockMonitor.Application.Market;
using AStockMonitor.Domain.Market;
using AStockMonitor.Infrastructure.Configuration;
using StackExchange.Redis;

namespace AStockMonitor.Infrastructure.Market;

/// <summary>
/// Persists one shard-local Tick batch with one Redis Lua invocation. Stream
/// append, replay watermark, latest quote projection and TTL move together.
/// </summary>
internal sealed class RedisTickStreamPublisher(
    MarketOptions options,
    RedisConnectionProvider connectionProvider) : IReliableTickPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string BatchScript = """
        local ttl = tonumber(ARGV[1])
        local max_length = tonumber(ARGV[2])
        local now_ms = tonumber(ARGV[3])
        local max_age_ms = tonumber(ARGV[4])
        local watermark_field = ARGV[5]
        local count = tonumber(ARGV[6])
        local watermark = tonumber(redis.call('HGET', KEYS[4], watermark_field) or '0')
        local accepted = 0
        local duplicate = 0
        local expired = 0
        local last_id = ''

        for index = 0, count - 1 do
            local base = 7 + index * 7
            local event_id = ARGV[base]
            local symbol = ARGV[base + 1]
            local sequence = tonumber(ARGV[base + 2])
            local event_time_ms = tonumber(ARGV[base + 3])
            local receive_time_ms = tonumber(ARGV[base + 4])
            local source_priority = tonumber(ARGV[base + 5])
            local tick_payload = ARGV[base + 6]

            if sequence <= watermark then
                duplicate = duplicate + 1
            elseif event_time_ms < now_ms - max_age_ms
                or event_time_ms > now_ms + 30000
                or receive_time_ms < now_ms - max_age_ms
                or receive_time_ms > now_ms + 30000 then
                expired = expired + 1
                watermark = sequence
            else
                last_id = redis.call(
                    'XADD', KEYS[1], 'MAXLEN', '~', max_length, '*',
                    'event_id', event_id,
                    'symbol', symbol,
                    'worker_sequence', sequence,
                    'payload', tick_payload)

                local old_meta = redis.call('HGET', KEYS[3], symbol)
                local should_update = false
                if not old_meta then
                    should_update = true
                else
                    local separator = string.find(old_meta, '|', 1, true)
                    local separator2 = separator and string.find(old_meta, '|', separator + 1, true)
                    local old_time = tonumber(string.sub(old_meta, 1, separator - 1))
                    local old_priority
                    local old_sequence
                    if separator2 then
                        old_priority = tonumber(string.sub(old_meta, separator + 1, separator2 - 1)) or 0
                        old_sequence = tonumber(string.sub(old_meta, separator2 + 1)) or 0
                    else
                        -- Backward compatibility with V3 meta: eventTime|sequence.
                        old_priority = 0
                        old_sequence = tonumber(string.sub(old_meta, separator + 1)) or 0
                    end
                    should_update = event_time_ms > old_time
                        or (event_time_ms == old_time and source_priority > old_priority)
                        or (event_time_ms == old_time and source_priority == old_priority
                            and sequence >= old_sequence)
                end

                if should_update then
                    redis.call('HSET', KEYS[2], symbol, tick_payload)
                    redis.call(
                        'HSET', KEYS[3], symbol,
                        tostring(event_time_ms) .. '|' .. tostring(source_priority)
                            .. '|' .. tostring(sequence))
                end
                watermark = sequence
                accepted = accepted + 1
            end
        end

        redis.call('HSET', KEYS[4], watermark_field, watermark)
        redis.call('EXPIRE', KEYS[1], ttl)
        redis.call('EXPIRE', KEYS[2], ttl)
        redis.call('EXPIRE', KEYS[3], ttl)
        redis.call('EXPIRE', KEYS[4], ttl)
        return tostring(accepted) .. '|' .. tostring(duplicate) .. '|'
            .. tostring(expired) .. '|' .. last_id
        """;

    public async Task<DurablePublishReceipt> PublishAsync(
        TickEvent tick,
        CancellationToken cancellationToken)
    {
        var result = await PublishBatchAsync(
            new TickPublishBatch(
                tick.EventId,
                options.GetV3TickShard(tick.Symbol),
                [tick]),
            cancellationToken);
        return new DurablePublishReceipt(result.Appended, result.LastStreamId, result.Reason);
    }

    public async Task<DurableBatchPublishReceipt> PublishBatchAsync(
        TickPublishBatch batch,
        CancellationToken cancellationToken)
    {
        if (!options.DurableIngestEnabled)
        {
            return new DurableBatchPublishReceipt(
                false, 0, 0, 0, batch.Ticks.Count, null, "Durable ingest is disabled");
        }

        if (batch.Ticks.Count == 0)
        {
            return new DurableBatchPublishReceipt(
                false, 0, 0, 0, 0, null, "Tick batch is empty");
        }

        if (batch.Ticks.Count > options.TickBatchMaxSize)
        {
            return new DurableBatchPublishReceipt(
                false,
                0,
                0,
                0,
                batch.Ticks.Count,
                null,
                $"Tick batch exceeds {options.TickBatchMaxSize}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var first = batch.Ticks[0];
        var tradingDate = ChinaMarketSession.TradingDate(first.EventTime);
        if (batch.ShardId < 0 || batch.ShardId >= options.TickV3ShardCount ||
            batch.Ticks.Any(tick =>
                options.GetV3TickShard(tick.Symbol) != batch.ShardId ||
                ChinaMarketSession.TradingDate(tick.EventTime) != tradingDate))
        {
            return new DurableBatchPublishReceipt(
                false, 0, 0, 0, batch.Ticks.Count, null,
                "Tick batch must contain one configured shard and trading date");
        }

        var now = DateTimeOffset.UtcNow;
        var arguments = new List<RedisValue>(6 + batch.Ticks.Count * 7)
        {
            Math.Max(14 * 60 * 60, options.LatestQuoteTtlSeconds),
            Math.Max(10_000, options.TickStreamMaxLengthPerShard),
            now.ToUnixTimeMilliseconds(),
            Math.Max(10, options.TickMaxAgeSeconds) * 1_000L,
            $"{first.WorkerId}|{first.SessionId}",
            batch.Ticks.Count
        };

        foreach (var tick in batch.Ticks.OrderBy(static tick => tick.WorkerSequence))
        {
            arguments.Add(tick.EventId);
            arguments.Add(tick.Symbol);
            arguments.Add(tick.WorkerSequence);
            arguments.Add(tick.EventTime.ToUnixTimeMilliseconds());
            arguments.Add(tick.ReceiveTime.ToUnixTimeMilliseconds());
            arguments.Add(Math.Clamp(tick.SourcePriority, 1, 1_000));
            arguments.Add(JsonSerializer.Serialize(tick, JsonOptions));
        }

        var connection = await connectionProvider.GetAsync();
        var database = connection.GetDatabase();
        var result = await database.ScriptEvaluateAsync(
            BatchScript,
            [
                options.GetV3TickStreamKey(tradingDate, batch.ShardId),
                options.GetV3TickLatestKey(tradingDate, batch.ShardId),
                options.GetV3TickLatestMetaKey(tradingDate, batch.ShardId),
                options.GetV3TickWatermarkKey(tradingDate, batch.ShardId)
            ],
            arguments.ToArray());

        var parts = result.ToString().Split('|', 4);
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var accepted) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var duplicate) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expired))
        {
            return new DurableBatchPublishReceipt(
                false, 0, 0, 0, batch.Ticks.Count, null,
                "Redis returned an invalid Tick batch receipt");
        }

        return new DurableBatchPublishReceipt(
            true,
            accepted,
            duplicate,
            expired,
            0,
            string.IsNullOrWhiteSpace(parts[3]) ? null : parts[3]);
    }
}
