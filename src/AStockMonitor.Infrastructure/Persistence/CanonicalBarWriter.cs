using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AStockMonitor.Application.Market;
using AStockMonitor.Contracts.Market;
using AStockMonitor.Domain.Market;
using AStockMonitor.Infrastructure.Market;
using Dapper;
using StackExchange.Redis;

namespace AStockMonitor.Infrastructure.Persistence;

/// <summary>
/// Canonical V2 writer. A closed official bar, its checkpoint and reliable
/// lifecycle event are committed in one MySQL transaction.
/// </summary>
public sealed class CanonicalBarWriter(
    IMySqlConnectionFactory connectionFactory,
    RedisConnectionProvider redis) : ICanonicalBarWriter
{
    public async Task<CanonicalBarWriteResult> WriteAsync(
        CanonicalBarInput input,
        CancellationToken cancellationToken)
    {
        Validate(input);
        var normalized = Normalize(input);
        if (!normalized.IsClosed)
        {
            await WriteActiveAsync(normalized, cancellationToken);
            return new CanonicalBarWriteResult(false, true, "BarUpdated", 0);
        }

        var mapping = Mapping.For(normalized.Frequency);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existing = await connection.QuerySingleOrDefaultAsync<ExistingBar>(new CommandDefinition(
            $"""
            SELECT row_hash RowHash, revision Revision, source_priority SourcePriority
            FROM {mapping.Table}
            WHERE symbol=@Symbol AND trading_date=@TradingDate AND eob=@Eob
              {mapping.FrequencyPredicate}
            FOR UPDATE;
            """,
            ToParameters(normalized, 0),
            transaction,
            cancellationToken: cancellationToken));

        var contentChanged = existing is null || !existing.RowHash.Equals(
            normalized.RowHash, StringComparison.OrdinalIgnoreCase);
        var changed = contentChanged && (existing is null || existing.SourcePriority <= 300);
        var revision = existing is null ? 0 : changed ? existing.Revision + 1 : existing.Revision;

        if (existing is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                mapping.InsertSql,
                ToParameters(normalized, revision),
                transaction,
                cancellationToken: cancellationToken));
        }
        else if (changed && existing.SourcePriority <= 300)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"""
                UPDATE {mapping.Table}
                SET bob=@Bob, open_price=@OpenPrice, high_price=@HighPrice,
                    low_price=@LowPrice, close_price=@ClosePrice, pre_close=@PreClose,
                    volume=@Volume, amount=@Amount, source=@Source,
                    source_priority=300, source_updated_at=@SourceUpdatedAt,
                    official_confirmed=TRUE, revision=@Revision,
                    quality_status='passed', recovery_run_id=@RecoveryRunId,
                    row_hash=@RowHash, updated_at=CURRENT_TIMESTAMP(6)
                WHERE symbol=@Symbol AND trading_date=@TradingDate AND eob=@Eob
                  {mapping.FrequencyPredicate};
                """,
                ToParameters(normalized, revision),
                transaction,
                cancellationToken: cancellationToken));
        }

        await UpsertCheckpointAsync(connection, transaction, normalized, cancellationToken);

        string? eventType = null;
        if (changed)
        {
            eventType = existing is null ? "BarClosed" : "BarRevised";
            var eventId = EventId(normalized, revision);
            var occurredAt = DateTimeOffset.UtcNow;
            var payload = BarLifecycleEventV2Json.Serialize(new BarLifecycleEventV2(
                BarLifecycleEventV2.CurrentSchemaVersion,
                eventId,
                eventType,
                normalized.Symbol,
                normalized.Frequency,
                normalized.TradingDate,
                normalized.Bob,
                normalized.Eob,
                revision,
                normalized.RowHash,
                normalized.Source,
                normalized.SourceUpdatedAt,
                true,
                normalized.CollectionMode,
                normalized.RecoveryRunId,
                occurredAt,
                new BarLifecyclePayloadV2(
                    normalized.OpenPrice,
                    normalized.HighPrice,
                    normalized.LowPrice,
                    normalized.ClosePrice,
                    normalized.PreClose,
                    normalized.Volume,
                    normalized.Amount)));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO bar_event_outbox
                    (event_id, event_type, symbol, frequency, trading_date, bob, eob,
                     revision, row_hash, payload, status)
                VALUES
                    (@EventId, @EventType, @Symbol, @Frequency, @TradingDate, @Bob, @Eob,
                     @Revision, @RowHash, CAST(@Payload AS JSON), 'pending')
                ON DUPLICATE KEY UPDATE event_id=event_id;
                """,
                new
                {
                    EventId = eventId,
                    EventType = eventType,
                    normalized.Symbol,
                    normalized.Frequency,
                    TradingDate = normalized.TradingDate.ToDateTime(TimeOnly.MinValue),
                    Bob = ToChinaLocal(normalized.Bob),
                    Eob = ToChinaLocal(normalized.Eob),
                    Revision = revision,
                    normalized.RowHash,
                    Payload = payload
                },
                transaction,
                cancellationToken: cancellationToken));

            if (existing is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO bar_reconcile_log
                        (reconcile_key, symbol, frequency, trading_date, eob, result_type,
                         old_row_hash, new_row_hash, reason, recovery_run_id)
                    VALUES
                        (@Key, @Symbol, @Frequency, @TradingDate, @Eob, 'source_mismatch',
                         @OldHash, @NewHash, @Reason, @RecoveryRunId)
                    ON DUPLICATE KEY UPDATE checked_at=CURRENT_TIMESTAMP(6);
                    """,
                    new
                    {
                        Key = Sha256($"reconcile|{normalized.Symbol}|{normalized.Frequency}|{normalized.Eob:O}|{normalized.RowHash}"),
                        normalized.Symbol,
                        normalized.Frequency,
                        TradingDate = normalized.TradingDate.ToDateTime(TimeOnly.MinValue),
                        Eob = ToChinaLocal(normalized.Eob),
                        OldHash = existing.RowHash,
                        NewHash = normalized.RowHash,
                        Reason = normalized.CollectionMode,
                        normalized.RecoveryRunId
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new CanonicalBarWriteResult(true, changed, eventType, revision);
    }

    private async Task WriteActiveAsync(CanonicalBarInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bar = new MarketBar(
            input.Symbol, input.Frequency, input.TradingDate, input.Bob, input.Eob,
            input.OpenPrice, input.HighPrice, input.LowPrice, input.ClosePrice,
            input.PreClose, input.Volume, input.Amount, false, false, false, 0,
            input.Source, false, input.Bob, input.Eob, input.RowHash, 300,
            input.RecoveryRunId, false, "preview", null, input.SourceUpdatedAt);
        var payload = JsonSerializer.Serialize(bar, BarLifecycleEventV2Json.Options);
        var database = (await redis.GetAsync()).GetDatabase();
        var key = $"md:v2:bar:active:{input.TradingDate:yyyy-MM-dd}:{input.Frequency}:{input.Symbol}";
        await database.StringSetAsync(key, payload, TimeSpan.FromHours(72));
        await database.PublishAsync(RedisChannel.Literal("md:v2:bar:updated"), payload);
    }

    private static async Task UpsertCheckpointAsync(
        MySqlConnector.MySqlConnection connection,
        MySqlConnector.MySqlTransaction transaction,
        CanonicalBarInput input,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO bar_sync_checkpoint
                (symbol, frequency, last_seen_eob, last_closed_eob,
                 last_persisted_eob, last_source_updated_at, status)
            VALUES
                (@Symbol, @Frequency, @Eob, @Eob, @Eob, @SourceUpdatedAt, 'healthy')
            ON DUPLICATE KEY UPDATE
                last_seen_eob=GREATEST(COALESCE(last_seen_eob,VALUES(last_seen_eob)),VALUES(last_seen_eob)),
                last_closed_eob=GREATEST(COALESCE(last_closed_eob,VALUES(last_closed_eob)),VALUES(last_closed_eob)),
                last_persisted_eob=GREATEST(COALESCE(last_persisted_eob,VALUES(last_persisted_eob)),VALUES(last_persisted_eob)),
                last_source_updated_at=VALUES(last_source_updated_at),
                status='healthy', consecutive_failures=0;
            """,
            new
            {
                input.Symbol,
                input.Frequency,
                Eob = ToChinaLocal(input.Eob),
                SourceUpdatedAt = ToChinaLocal(input.SourceUpdatedAt)
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static object ToParameters(CanonicalBarInput input, int revision) => new
    {
        input.Symbol,
        input.Frequency,
        TradingDate = input.TradingDate.ToDateTime(TimeOnly.MinValue),
        Bob = ToChinaLocal(input.Bob),
        Eob = ToChinaLocal(input.Eob),
        input.OpenPrice,
        input.HighPrice,
        input.LowPrice,
        input.ClosePrice,
        input.PreClose,
        input.Volume,
        input.Amount,
        input.Source,
        SourceUpdatedAt = ToChinaLocal(input.SourceUpdatedAt),
        Revision = revision,
        input.RecoveryRunId,
        input.RowHash
    };

    private static CanonicalBarInput Normalize(CanonicalBarInput input)
    {
        var symbol = input.Symbol.Trim().ToUpperInvariant();
        var frequency = input.Frequency.Trim().ToLowerInvariant();
        var source = string.IsNullOrWhiteSpace(input.Source) ? "dongcai-gm" : input.Source.Trim();
        var normalized = input with { Symbol = symbol, Frequency = frequency, Source = source };
        return string.IsNullOrWhiteSpace(normalized.RowHash)
            ? normalized with { RowHash = Sha256(FactText(normalized)) }
            : normalized;
    }

    private static void Validate(CanonicalBarInput input)
    {
        if (!MarketBarFrequencies.IsSupported(input.Frequency))
            throw new ArgumentException("Official Bar supports only 5m,30m,60m,1d.");
        if (string.IsNullOrWhiteSpace(input.Symbol)) throw new ArgumentException("Symbol is required.");
        if (input.OpenPrice <= 0 || input.HighPrice <= 0 || input.LowPrice <= 0 || input.ClosePrice <= 0
            || input.HighPrice < Math.Max(input.OpenPrice, input.ClosePrice)
            || input.LowPrice > Math.Min(input.OpenPrice, input.ClosePrice)
            || input.HighPrice < input.LowPrice || input.Volume < 0 || input.Amount < 0)
            throw new ArgumentException("Official Bar failed OHLC/volume validation.");
    }

    private static string EventId(CanonicalBarInput input, int revision) =>
        "sha256:" + Sha256(
            $"official|{input.Symbol}|{input.Frequency}|{input.Eob:O}|{revision}|{input.RowHash}");
    private static string FactText(CanonicalBarInput input) =>
        string.Join('|', input.Symbol, input.Frequency, input.TradingDate, input.Bob, input.Eob,
            input.OpenPrice, input.HighPrice, input.LowPrice, input.ClosePrice, input.PreClose,
            input.Volume, input.Amount, input.Source);
    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static DateTime ToChinaLocal(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, ChinaMarketSession.TimeZone).DateTime;

    private sealed class ExistingBar
    {
        public string RowHash { get; init; } = string.Empty;
        public int Revision { get; init; }
        public int SourcePriority { get; init; }
    }

    private sealed record Mapping(string Table, string FrequencyPredicate, string InsertSql)
    {
        public static Mapping For(string frequency) => frequency switch
        {
            "5m" => Simple("kline_bar_5m"),
            "1d" => Simple("kline_bar_daily"),
            "30m" or "60m" => Aggregate(),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency))
        };

        private static Mapping Simple(string table) => new(table, string.Empty, $"""
            INSERT INTO {table}
                (symbol,trading_date,bob,eob,open_price,high_price,low_price,close_price,
                 pre_close,volume,amount,source,adjust_mode,source_priority,source_updated_at,
                 official_confirmed,revision,quality_status,recovery_run_id,row_hash)
            VALUES
                (@Symbol,@TradingDate,@Bob,@Eob,@OpenPrice,@HighPrice,@LowPrice,@ClosePrice,
                 @PreClose,@Volume,@Amount,@Source,'none',300,@SourceUpdatedAt,
                 TRUE,@Revision,'passed',@RecoveryRunId,@RowHash);
            """);

        private static Mapping Aggregate() => new("kline_bar_agg", "AND frequency=@Frequency", """
            INSERT INTO kline_bar_agg
                (symbol,frequency,trading_date,bob,eob,open_price,high_price,low_price,
                 close_price,pre_close,volume,amount,component_count,expected_component_count,
                 source,source_priority,source_updated_at,official_confirmed,revision,
                 quality_status,algorithm_version,recovery_run_id,row_hash)
            VALUES
                (@Symbol,@Frequency,@TradingDate,@Bob,@Eob,@OpenPrice,@HighPrice,@LowPrice,
                 @ClosePrice,@PreClose,@Volume,@Amount,1,1,@Source,300,@SourceUpdatedAt,
                 TRUE,@Revision,'passed','official-gm-v2',@RecoveryRunId,@RowHash);
            """);
    }
}
