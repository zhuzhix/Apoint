using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Domain.Analytics;
using AStockMonitor.Domain.Market;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Infrastructure.Analytics;

/// <summary>只接收内存预筛后的潜在突破 Tick；一次事务可同时失效同股票多个价位。</summary>
public sealed class PairTrendTickInvalidationService(
    IMySqlConnectionFactory connectionFactory,
    IPairTrendActiveLevelCache activeLevelCache) : IPairTrendTickInvalidationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvalidateAsync(
        TickEvent tick,
        IReadOnlyCollection<long> candidateEventIds,
        CancellationToken cancellationToken)
    {
        if (candidateEventIds.Count == 0)
            return;
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT id Id,event_key EventKey,symbol Symbol,symbol_name SymbolName,
                   pivot_type PivotType,stage Stage,latest_pair_price PairPrice,
                   price_ticks PriceTicks,latest_pair_code PairCode,
                   latest_pair_kind PairKind,generation Generation,event_revision EventRevision,
                   first_seen_at FirstSeenAt,frequencies Frequencies,
                   strongest_frequency StrongestFrequency
            FROM pair_trend_live_event
            WHERE id IN @Ids AND symbol=@Symbol AND is_active=TRUE
              AND algorithm_version=@AlgorithmVersion
            ORDER BY id FOR UPDATE;
            """,
            new
            {
                Ids = candidateEventIds.ToArray(),
                Symbol = tick.Symbol.Trim().ToUpperInvariant(),
                AlgorithmVersion = PairTrendOptions.CurrentAlgorithmVersion
            }, transaction, cancellationToken: cancellationToken))).AsList();

        foreach (var row in rows)
        {
            var broken = row.PivotType == "TOP" ? tick.Price > row.PairPrice : tick.Price < row.PairPrice;
            if (!broken || tick.EventTime.DateTime <= row.FirstSeenAt)
                continue;
            var reason = row.PivotType == "TOP" ? "TICK_HIGHER_PRICE_BREAK" : "TICK_LOWER_PRICE_BREAK";
            var occurredAt = tick.EventTime.DateTime;
            var sourceHash = Hash(string.Join('|', tick.EventId, tick.Symbol,
                tick.EventTime.ToString("O"), tick.Price));
            var lifecycleKey = Hash(string.Join('|', row.EventKey, row.Stage,
                "INVALIDATED", tick.EventId, sourceHash));
            var summary = JsonSerializer.Serialize(new
            {
                row.EventKey, row.Symbol, row.PivotType, stage = "INVALIDATED",
                isActive = false, pairPrice = row.PairPrice, row.PriceTicks,
                row.Generation, invalidatedAt = occurredAt, invalidatedPrice = tick.Price,
                invalidationReason = reason, row.Frequencies, row.StrongestFrequency,
                algorithmVersion = PairTrendOptions.CurrentAlgorithmVersion
            }, JsonOptions);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE pair_trend_live_event SET
                    status='INVALIDATED',stage='INVALIDATED',is_active=FALSE,
                    invalidated_at=@OccurredAt,invalidated_price=@Price,
                    invalidation_reason=@Reason,last_seen_at=@OccurredAt,
                    last_transition_at=@OccurredAt,event_revision=event_revision+1,
                    content_hash=@ContentHash,last_source_event_id=@TickEventId,
                    summary_json=CAST(@Summary AS JSON),invalidated_hit_count=1,
                    pending_hit_count=0
                WHERE id=@Id AND is_active=TRUE;
                """,
                new
                {
                    row.Id, OccurredAt = occurredAt, Price = tick.Price, Reason = reason,
                    ContentHash = Hash(summary), TickEventId = tick.EventId, Summary = summary
                }, transaction, cancellationToken: cancellationToken));
            var shouldNotify = StageRank(row.Stage) >= StageRank("OBSERVING");
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT IGNORE INTO pair_trend_live_lifecycle
                    (event_id,lifecycle_key,symbol,from_stage,to_stage,occurred_at,
                     trigger_frequency,trigger_price,reason,source_row_hash,should_notify)
                VALUES
                    (@EventId,@LifecycleKey,@Symbol,@FromStage,'INVALIDATED',@OccurredAt,
                     'tick',@Price,@Reason,@SourceHash,@ShouldNotify);
                """,
                new
                {
                    EventId = row.Id, LifecycleKey = lifecycleKey, row.Symbol,
                    FromStage = row.Stage, OccurredAt = occurredAt, Price = tick.Price,
                    Reason = reason, SourceHash = sourceHash, ShouldNotify = shouldNotify
                }, transaction, cancellationToken: cancellationToken));
            if (!shouldNotify)
                continue;
            var payload = JsonSerializer.Serialize(new
            {
                eventId = row.Id, row.EventKey, row.Symbol, row.SymbolName,
                row.PivotType, stage = "INVALIDATED", status = "INVALIDATED",
                pairPrice = row.PairPrice, row.PriceTicks, row.PairCode, row.PairKind,
                row.Generation, occurredAt, triggerFrequency = "tick",
                triggerPrice = tick.Price, reason, alertLevel = 4,
                algorithmVersion = PairTrendOptions.CurrentAlgorithmVersion
            }, JsonOptions);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT IGNORE INTO pair_trend_event_outbox
                    (outbox_event_id,event_id,event_key,symbol,lifecycle_type,
                     event_revision,payload,status)
                VALUES
                    (@OutboxEventId,@EventId,@EventKey,@Symbol,'INVALIDATED',
                     @Revision,CAST(@Payload AS JSON),'pending');
                """,
                new
                {
                    OutboxEventId = "sha256:" + Hash($"pair|{lifecycleKey}|{Hash(payload)}"),
                    EventId = row.Id, row.EventKey, row.Symbol,
                    Revision = row.EventRevision + 1, Payload = payload
                }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        await activeLevelCache.ReloadSymbolAsync(tick.Symbol, cancellationToken);
    }

    private static int StageRank(string stage) => stage switch
    {
        "DISCOVERED" => 1, "OBSERVING" => 2, "FOCUS" => 3,
        "ESTABLISHED" => 4, _ => 9
    };

    private static string Hash(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class Row
    {
        public long Id { get; init; }
        public string EventKey { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string? SymbolName { get; init; }
        public string PivotType { get; init; } = string.Empty;
        public string Stage { get; init; } = string.Empty;
        public decimal PairPrice { get; init; }
        public long PriceTicks { get; init; }
        public int PairCode { get; init; }
        public string PairKind { get; init; } = string.Empty;
        public int Generation { get; init; }
        public int EventRevision { get; init; }
        public DateTime FirstSeenAt { get; init; }
        public string Frequencies { get; init; } = string.Empty;
        public string StrongestFrequency { get; init; } = string.Empty;
    }
}
