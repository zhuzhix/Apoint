namespace AStockMonitor.Infrastructure.Configuration;

/// <summary>进程角色，用于执行不可静默降级的行情配置检查。</summary>
public enum MarketHostRole
{
    Api,
    Worker,
    StrategyScanner
}

/// <summary>正式行情链路的启动前配置保护。</summary>
public static class MarketConfigurationValidator
{
    public static void Validate(
        MarketOptions options,
        MarketHostRole role,
        bool redisPipelineEnabled = true)
    {
        var errors = new List<string>();
        if (options.TickMySqlPersistenceEnabled)
            errors.Add("Market:TickMySqlPersistenceEnabled must remain false in V2.");
        if (redisPipelineEnabled)
        {
            if (role == MarketHostRole.Api && !options.DurableIngestEnabled)
                errors.Add("API requires Market:DurableIngestEnabled=true so accepted ticks are durable.");
            if (!string.Equals(options.BarEventV2KeyPrefix, "md:v2:bar:event", StringComparison.Ordinal))
                errors.Add("Market:BarEventV2KeyPrefix must be exactly 'md:v2:bar:event'.");
            if (options.TickStreamShardCount is < 1 or > 256)
                errors.Add("Market:TickStreamShardCount must be between 1 and 256.");
            if (options.TickV3ShardCount is < 1 or > 256 ||
                (options.TickV3ShardCount & (options.TickV3ShardCount - 1)) != 0)
                errors.Add("Market:TickV3ShardCount must be a power of two between 1 and 256.");
            if (options.TickBatchMaxSize is < 1 or > 2_000)
                errors.Add("Market:TickBatchMaxSize must be between 1 and 2000.");
            if (options.TickMaxAgeSeconds < 10 ||
                options.TickMaxAgeSeconds > options.TickStreamHardRetentionMinutes * 60)
                errors.Add("Market:TickMaxAgeSeconds must be between 10 seconds and hard retention.");
            if (options.LatestQuoteTtlSeconds < 14 * 60 * 60)
                errors.Add("Market:LatestQuoteTtlSeconds must cover a complete trading day.");
            if (options.BarEventOutboxBatchSize is < 1 or > 2_000)
                errors.Add("Market:BarEventOutboxBatchSize must be between 1 and 2000.");
            if (options.BarEventOutboxLeaseSeconds is < 5 or > 600)
                errors.Add("Market:BarEventOutboxLeaseSeconds must be between 5 and 600.");
            if (options.BarEventOutboxMaxAttempts is < 1 or > 100)
                errors.Add("Market:BarEventOutboxMaxAttempts must be between 1 and 100.");
            if (options.BarEventStreamMaxLengthPerShard < 10_000)
                errors.Add("Market:BarEventStreamMaxLengthPerShard must be at least 10000.");
            if (string.IsNullOrWhiteSpace(options.RedisConnection))
                errors.Add("Market:RedisConnection is required.");
        }
        if (string.IsNullOrWhiteSpace(options.MySqlConnection))
            errors.Add("Market:MySqlConnection is required.");

        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Invalid market configuration for {role}:{Environment.NewLine}- " +
                string.Join($"{Environment.NewLine}- ", errors));
    }
}
