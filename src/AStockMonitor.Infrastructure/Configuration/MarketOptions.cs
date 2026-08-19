using AStockMonitor.Application.Market;

namespace AStockMonitor.Infrastructure.Configuration;

public sealed class MarketOptions
{
    /// <summary>Whether the API must append every accepted Tick to Redis Streams.</summary>
    public bool DurableIngestEnabled { get; set; }
    /// <summary>V2 never persists raw Tick records in MySQL.</summary>
    public bool TickMySqlPersistenceEnabled { get; set; }
    public bool PersistenceEnabled { get; set; }
    public string RedisConnection { get; set; } = "localhost:6379,password=change-me";
    public string MySqlConnection { get; set; } = "Server=localhost;Port=3306;Database=astock_monitor;User ID=astock_app;Password=change-me;SslMode=None;AllowPublicKeyRetrieval=True;";
    public string TickStreamKeyPrefix { get; set; } = "dev:stream:market:raw:tick";
    public string TickV2KeyPrefix { get; set; } = "md:v2:tick";
    public string TickV3KeyPrefix { get; set; } = "md:tick:v3";
    public int TickProtocolVersion { get; set; } = 3;
    public int TickV3ShardCount { get; set; } = 64;
    public int TickBatchMaxSize { get; set; } = 500;
    public int TickMaxAgeSeconds { get; set; } = 120;
    public string BarEventV2KeyPrefix { get; set; } = "md:v2:bar:event";
    public int BarEventOutboxBatchSize { get; set; } = 200;
    public int BarEventOutboxLeaseSeconds { get; set; } = 30;
    public int BarEventOutboxMaxAttempts { get; set; } = 10;
    public long BarEventStreamMaxLengthPerShard { get; set; } = 2_000_000;
    public int TickStreamShardCount { get; set; } = 16;
    public bool TickStreamRetentionEnabled { get; set; } = true;
    public int TickStreamRetentionMinutes { get; set; } = 3;
    public int TickStreamHardRetentionMinutes { get; set; } = 3;
    public long TickStreamMaxLengthPerShard { get; set; } = 1_000_000;
    public string PersistenceConsumerGroup { get; set; } = "mysql-writer";
    public int BatchSize { get; set; } = 500;
    public int FlushIntervalMs { get; set; } = 100;
    public int PendingMessageIdleMs { get; set; } = 60_000;
    public int PendingRecoveryIntervalMs { get; set; } = 30_000;
    public int LatestQuoteTtlSeconds { get; set; } = 129_600;
    public int RecentTicksPerSymbol { get; set; } = 256;
    public IReadOnlyList<string> GetTickStreamKeys()
    {
        var count = Math.Clamp(TickStreamShardCount, 1, 256);
        return count == 1
            ? [TickStreamKeyPrefix]
            : Enumerable.Range(0, count)
                .Select(index => $"{TickStreamKeyPrefix}:{index:D2}")
                .ToArray();
    }

    public string GetTickStreamKey(string symbol)
    {
        var keys = GetTickStreamKeys();
        return keys[StableShard(symbol, keys.Count)];
    }

    public string GetV2TickLatestKey(DateOnly tradingDate, string symbol) =>
        $"{TickV2KeyPrefix}:latest:{tradingDate:yyyy-MM-dd}:{symbol.Trim().ToUpperInvariant()}";

    public string GetV2TickStreamKey(DateOnly tradingDate, string symbol) =>
        $"{TickV2KeyPrefix}:stream:{tradingDate:yyyy-MM-dd}:" +
        $"{StableShard(symbol, Math.Clamp(TickStreamShardCount, 1, 256)):D2}";

    public IReadOnlyList<string> GetV2TickStreamKeys(DateOnly tradingDate)
    {
        var count = Math.Clamp(TickStreamShardCount, 1, 256);
        return Enumerable.Range(0, count)
            .Select(index => $"{TickV2KeyPrefix}:stream:{tradingDate:yyyy-MM-dd}:{index:D2}")
            .ToArray();
    }

    public string GetV3TickStreamKey(DateOnly tradingDate, int shard) =>
        $"{TickV3KeyPrefix}:{{{tradingDate:yyyyMMdd}:{NormalizeV3Shard(shard):D2}}}:stream";

    public string GetV3TickLatestKey(DateOnly tradingDate, int shard) =>
        $"{TickV3KeyPrefix}:{{{tradingDate:yyyyMMdd}:{NormalizeV3Shard(shard):D2}}}:latest";

    public string GetV3TickLatestMetaKey(DateOnly tradingDate, int shard) =>
        $"{TickV3KeyPrefix}:{{{tradingDate:yyyyMMdd}:{NormalizeV3Shard(shard):D2}}}:latest-meta";

    public string GetV3TickWatermarkKey(DateOnly tradingDate, int shard) =>
        $"{TickV3KeyPrefix}:{{{tradingDate:yyyyMMdd}:{NormalizeV3Shard(shard):D2}}}:watermark";

    public int GetV3TickShard(string symbol) =>
        StableShard(symbol, Math.Clamp(TickV3ShardCount, 1, 256));

    private int NormalizeV3Shard(int shard) =>
        Math.Clamp(shard, 0, Math.Clamp(TickV3ShardCount, 1, 256) - 1);

    /// <summary>返回官方 K 线 V2 可靠事件分片；不读取任何 V1 配置。</summary>
    public string GetBarEventV2StreamKey(string symbol) =>
        GetBarEventV2StreamKey(StableShard(
            symbol, Math.Clamp(TickStreamShardCount, 1, 256)));

    /// <summary>按分片号返回官方 K 线 V2 可靠事件 Stream。</summary>
    public string GetBarEventV2StreamKey(int shard)
    {
        var count = Math.Clamp(TickStreamShardCount, 1, 256);
        return count == 1
            ? BarEventV2KeyPrefix
            : $"{BarEventV2KeyPrefix}:{Math.Clamp(shard, 0, count - 1):D2}";
    }

    public static int StableShard(string value, int shardCount)
    {
        // FNV-1a is deliberately used instead of string.GetHashCode(), whose
        // randomized result changes between .NET processes.
        var hash = 2166136261u;
        foreach (var character in value.ToUpperInvariant())
        {
            hash ^= character;
            hash *= 16777619u;
        }

        return (int)(hash % (uint)shardCount);
    }
}
