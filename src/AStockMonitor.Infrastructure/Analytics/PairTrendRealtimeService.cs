using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Contracts.Market;
using AStockMonitor.Domain.Analytics;
using AStockMonitor.Infrastructure.Configuration;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;
using MySqlConnector;

namespace AStockMonitor.Infrastructure.Analytics;

/// <summary>
/// V3 盘中对子状态机。5m 负责发现和严格突破失效，30m/60m/1d 按同方向、
/// 同价格逐级升级；业务状态、审计和待发布消息在同一个 MySQL 事务内提交。
/// </summary>
public sealed class PairTrendRealtimeService(
    IMySqlConnectionFactory connectionFactory,
    MarketOptions marketOptions,
    IPairTrendActiveLevelCache activeLevelCache) : IPairTrendRealtimeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PairTrendOptions _options = new();

    public async Task ProcessAsync(
        BarLifecycleEventV2 barEvent,
        int shard,
        string streamMessageId,
        CancellationToken cancellationToken)
    {
        barEvent.Validate();
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, cancellationToken);

        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT IGNORE INTO pair_trend_processed_event
                (source_event_id,shard,stream_message_id)
            VALUES (@EventId,@Shard,@MessageId);
            """,
            new { barEvent.EventId, Shard = shard, MessageId = streamMessageId },
            transaction, cancellationToken: cancellationToken));
        if (inserted == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var streamKey = marketOptions.GetBarEventV2StreamKey(shard);
        await EnsureAndLockCheckpointAsync(
            connection, transaction, barEvent, shard, streamKey, streamMessageId,
            cancellationToken);

        // 同一股票固定落入同一分片；数据库行锁同时防止恢复消费者与正常消费者重入。
        var active = (await connection.QueryAsync<LiveEventState>(new CommandDefinition(
            SelectActiveEventsSql,
            new { barEvent.Symbol, AlgorithmVersion = _options.AlgorithmVersion },
            transaction, cancellationToken: cancellationToken))).AsList();

        var effectiveEob = EffectiveEob(barEvent);
        if (barEvent.Frequency == "5m")
        {
            await InvalidateBrokenAsync(
                connection, transaction, barEvent, effectiveEob, active, cancellationToken);
            await DiscoverAsync(connection, transaction, barEvent, effectiveEob,
                PairPivotType.Top, barEvent.Bar.High, active, cancellationToken);
            await DiscoverAsync(connection, transaction, barEvent, effectiveEob,
                PairPivotType.Bottom, barEvent.Bar.Low, active, cancellationToken);
        }
        else
        {
            await PromoteAsync(
                connection, transaction, barEvent, effectiveEob, active, cancellationToken);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_consumer_checkpoint
            SET stream_key=@StreamKey,last_message_id=@MessageId,
                last_source_event_id=@EventId,last_success_at=CURRENT_TIMESTAMP(6),
                processed_count=processed_count+1,last_error=NULL
            WHERE shard=@Shard;
            """,
            new { Shard = shard, StreamKey = streamKey, MessageId = streamMessageId, barEvent.EventId },
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        await activeLevelCache.ReloadSymbolAsync(barEvent.Symbol, cancellationToken);
    }

    private async Task InvalidateBrokenAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        BarLifecycleEventV2 source,
        DateTime occurredAt,
        ICollection<LiveEventState> active,
        CancellationToken cancellationToken)
    {
        foreach (var state in active.Where(item => item.IsActive).ToArray())
        {
            var breakPrice = state.PivotType == "TOP" ? source.Bar.High : source.Bar.Low;
            var broken = state.PivotType == "TOP"
                ? breakPrice > state.LatestPairPrice
                : breakPrice < state.LatestPairPrice;
            if (!broken || occurredAt <= state.DiscoveredAt)
                continue;

            var previous = state.Stage;
            var reason = state.PivotType == "TOP" ? "HIGHER_PRICE_BREAK" : "LOWER_PRICE_BREAK";
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE pair_trend_live_event SET
                    status='INVALIDATED',stage='INVALIDATED',is_active=FALSE,
                    invalidated_at=@OccurredAt,invalidated_price=@BreakPrice,
                    invalidation_reason=@Reason,last_seen_at=@OccurredAt,
                    last_transition_at=@OccurredAt,last_source_event_id=@EventId
                WHERE id=@Id AND is_active=TRUE;
                """,
                new { state.Id, OccurredAt = occurredAt, BreakPrice = breakPrice, Reason = reason, source.EventId },
                transaction, cancellationToken: cancellationToken));
            state.Stage = "INVALIDATED";
            state.IsActive = false;
            await WriteLifecycleAsync(connection, transaction, source, state, previous,
                "INVALIDATED", occurredAt, breakPrice, reason,
                StageRank(previous) >= StageRank("OBSERVING"), cancellationToken);
            await RefreshEventAsync(connection, transaction, source, state, cancellationToken);
        }
    }

    private async Task DiscoverAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        BarLifecycleEventV2 source,
        DateTime occurredAt,
        PairPivotType pivot,
        decimal price,
        ICollection<LiveEventState> active,
        CancellationToken cancellationToken)
    {
        var match = PairPriceMatcher.Match(price, _options.PriceTick, _options.IncludeRound00);
        if (match is null)
            return;
        var pivotText = Db(pivot);
        var state = active.FirstOrDefault(item => item.IsActive &&
            item.PivotType == pivotText && item.PriceTicks == match.PriceTicks);
        if (state is null)
        {
            var generation = await connection.QuerySingleAsync<int>(new CommandDefinition(
                """
                SELECT COALESCE(MAX(generation),0)+1 FROM pair_trend_live_event
                WHERE symbol=@Symbol AND pivot_type=@PivotType AND price_ticks=@PriceTicks
                  AND algorithm_version=@AlgorithmVersion;
                """,
                new
                {
                    source.Symbol, PivotType = pivotText, match.PriceTicks,
                    AlgorithmVersion = _options.AlgorithmVersion
                }, transaction, cancellationToken: cancellationToken));
            var eventKey = Hash(string.Join('|', source.Symbol, pivotText, match.PriceTicks,
                occurredAt.ToString("O"), generation, _options.AlgorithmVersion));
            var symbolName = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT name FROM instrument WHERE symbol=@Symbol;",
                new { source.Symbol }, transaction, cancellationToken: cancellationToken));
            var initialSummary = JsonSerializer.Serialize(new
            {
                eventKey, source.Symbol, pivotType = pivotText, stage = "DISCOVERED",
                pairPrice = match.Price, match.PriceTicks, generation
            }, JsonOptions);
            await connection.ExecuteAsync(new CommandDefinition(
                InsertEventSql,
                new
                {
                    EventKey = eventKey, source.Symbol, SymbolName = symbolName,
                    PivotType = pivotText, FirstSeenAt = occurredAt, LastSeenAt = occurredAt,
                    LatestPairPrice = match.Price, match.PriceTicks,
                    LatestPairCode = match.PairCode, LatestPairKind = Db(match.Kind),
                    AlgorithmVersion = _options.AlgorithmVersion, Generation = generation,
                    RootBob = source.Bob.DateTime, RootEob = source.Eob.DateTime,
                    LastSourceEventId = source.EventId, ContentHash = Hash(initialSummary),
                    SummaryJson = initialSummary
                }, transaction, cancellationToken: cancellationToken));
            var id = await connection.QuerySingleAsync<long>(new CommandDefinition(
                "SELECT LAST_INSERT_ID();", transaction: transaction,
                cancellationToken: cancellationToken));
            state = new LiveEventState
            {
                Id = id, EventKey = eventKey, Symbol = source.Symbol, SymbolName = symbolName,
                PivotType = pivotText, Stage = "DISCOVERED", IsActive = true,
                PriceTicks = match.PriceTicks, LatestPairPrice = match.Price,
                LatestPairCode = match.PairCode, LatestPairKind = Db(match.Kind),
                Generation = generation, DiscoveredAt = occurredAt
            };
            active.Add(state);
            await WriteLifecycleAsync(connection, transaction, source, state, null,
                "DISCOVERED", occurredAt, match.Price, "FIVE_MINUTE_DISCOVERY", false,
                cancellationToken);
        }

        await WriteHitAsync(connection, transaction, source, state, occurredAt,
            "DISCOVERED", false, "FIVE_MINUTE_DISCOVERY", cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_live_event
            SET last_seen_at=GREATEST(last_seen_at,@OccurredAt),last_source_event_id=@EventId
            WHERE id=@Id;
            """,
            new { state.Id, OccurredAt = occurredAt, source.EventId }, transaction,
            cancellationToken: cancellationToken));
        await RefreshEventAsync(connection, transaction, source, state, cancellationToken);
    }

    private async Task PromoteAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        BarLifecycleEventV2 source,
        DateTime occurredAt,
        IReadOnlyCollection<LiveEventState> active,
        CancellationToken cancellationToken)
    {
        var transition = source.Frequency switch
        {
            "30m" => (From: "DISCOVERED", To: "OBSERVING", Reason: "SAME_PRICE_30M"),
            "60m" => (From: "OBSERVING", To: "FOCUS", Reason: "SAME_PRICE_60M"),
            "1d" => (From: "FOCUS", To: "ESTABLISHED", Reason: "SAME_PRICE_1D"),
            _ => default
        };
        if (transition.Reason is null)
            return;

        foreach (var candidate in new[]
                 {
                     (Pivot: "TOP", Price: source.Bar.High),
                     (Pivot: "BOTTOM", Price: source.Bar.Low)
                 })
        {
            var match = PairPriceMatcher.Match(
                candidate.Price, _options.PriceTick, _options.IncludeRound00);
            if (match is null)
                continue;
            var state = active.FirstOrDefault(item => item.IsActive &&
                item.PivotType == candidate.Pivot && item.PriceTicks == match.PriceTicks &&
                item.Stage == transition.From && occurredAt >= item.DiscoveredAt);
            if (state is null)
                continue;

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE pair_trend_live_event SET
                    status='CONFIRMED',stage=@ToStage,last_seen_at=@OccurredAt,
                    confirmed_at=COALESCE(confirmed_at,@ObservedAt),
                    observed_at=IF(@ToStage='OBSERVING',@OccurredAt,observed_at),
                    focused_at=IF(@ToStage='FOCUS',@OccurredAt,focused_at),
                    established_at=IF(@ToStage='ESTABLISHED',@OccurredAt,established_at),
                    last_transition_at=@OccurredAt,last_source_event_id=@EventId
                WHERE id=@Id AND is_active=TRUE AND stage=@FromStage;
                """,
                new
                {
                    state.Id, ToStage = transition.To, FromStage = transition.From,
                    OccurredAt = occurredAt,
                    ObservedAt = transition.To == "OBSERVING" ? occurredAt : (DateTime?)null,
                    source.EventId
                }, transaction, cancellationToken: cancellationToken));
            var previous = state.Stage;
            state.Stage = transition.To;
            await WriteHitAsync(connection, transaction, source, state, occurredAt,
                transition.To, true, transition.Reason, cancellationToken);
            await WriteLifecycleAsync(connection, transaction, source, state, previous,
                transition.To, occurredAt, candidate.Price, transition.Reason, true,
                cancellationToken);
            await RefreshEventAsync(connection, transaction, source, state, cancellationToken);
        }
    }

    private async Task WriteHitAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        BarLifecycleEventV2 source,
        LiveEventState state,
        DateTime occurredAt,
        string stage,
        bool promotion,
        string reason,
        CancellationToken cancellationToken)
    {
        var hitKey = Hash(string.Join('|', state.EventKey, source.Frequency,
            source.Eob.ToString("O"), stage, _options.AlgorithmVersion));
        await connection.ExecuteAsync(new CommandDefinition(
            UpsertHitSql,
            new
            {
                EventId = state.Id, HitKey = hitKey, source.Symbol, source.Frequency,
                TradingDate = source.TradingDate.ToDateTime(TimeOnly.MinValue),
                Bob = source.Bob.DateTime, Eob = source.Eob.DateTime,
                ObservedAt = occurredAt, ConfirmedAt = promotion ? occurredAt : (DateTime?)null,
                state.PivotType, Status = promotion ? "CONFIRMED" : "CANDIDATE",
                PairPrice = state.LatestPairPrice, state.PriceTicks,
                PairCode = state.LatestPairCode, PairKind = state.LatestPairKind,
                HitField = state.PivotType == "TOP" ? "HIGH" : "LOW",
                source.Bar.Open, source.Bar.High, source.Bar.Low, source.Bar.Close,
                source.Bar.PreClose, source.Bar.Volume, source.Bar.Amount,
                Reason = reason, source.Revision, source.RowHash,
                SourceEventId = source.EventId, AlgorithmVersion = _options.AlgorithmVersion,
                Stage = stage, IsPromotion = promotion,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    stage, isPromotion = promotion, includeRound00 = _options.IncludeRound00
                }, JsonOptions)
            }, transaction, cancellationToken: cancellationToken));
    }

    private async Task WriteLifecycleAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        BarLifecycleEventV2 source,
        LiveEventState state,
        string? fromStage,
        string toStage,
        DateTime occurredAt,
        decimal triggerPrice,
        string reason,
        bool shouldNotify,
        CancellationToken cancellationToken)
    {
        // 补数事件必须重建对子状态和审计记录，但陈旧 K 线不能产生盘中警报。
        shouldNotify = shouldNotify && BarEventDeliveryPolicy.IsLiveNotificationEligible(
            source, DateTimeOffset.UtcNow);
        var lifecycleKey = Hash(string.Join('|', state.EventKey, fromStage, toStage,
            occurredAt.ToString("O"), reason, source.RowHash));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT IGNORE INTO pair_trend_live_lifecycle
                (event_id,lifecycle_key,symbol,from_stage,to_stage,occurred_at,
                 trigger_frequency,trigger_price,reason,source_row_hash,should_notify)
            VALUES
                (@EventId,@LifecycleKey,@Symbol,@FromStage,@ToStage,@OccurredAt,
                 @Frequency,@TriggerPrice,@Reason,@RowHash,@ShouldNotify);
            """,
            new
            {
                EventId = state.Id, LifecycleKey = lifecycleKey, source.Symbol,
                FromStage = fromStage, ToStage = toStage, OccurredAt = occurredAt,
                source.Frequency, TriggerPrice = triggerPrice, Reason = reason,
                source.RowHash, ShouldNotify = shouldNotify
            }, transaction, cancellationToken: cancellationToken));
        if (!shouldNotify)
            return;

        var payload = JsonSerializer.Serialize(new
        {
            eventId = state.Id, state.EventKey, source.Symbol, state.SymbolName,
            pivotType = state.PivotType, stage = toStage,
            status = toStage == "INVALIDATED" ? "INVALIDATED" : "CONFIRMED",
            pairPrice = state.LatestPairPrice, state.PriceTicks,
            pairCode = state.LatestPairCode, pairKind = state.LatestPairKind,
            generation = state.Generation, occurredAt, triggerFrequency = source.Frequency,
            triggerPrice, reason,
            alertLevel = toStage switch
            {
                "ESTABLISHED" => 1,
                "FOCUS" => 2,
                "OBSERVING" => 3,
                _ => 4
            },
            action = toStage == "ESTABLISHED"
                ? state.PivotType == "TOP" ? "SELL_REMINDER" : "BUY_REMINDER"
                : null,
            algorithmVersion = _options.AlgorithmVersion
        }, JsonOptions);
        var outboxEventId = "sha256:" + Hash($"pair|{lifecycleKey}|{Hash(payload)}");
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT IGNORE INTO pair_trend_event_outbox
                (outbox_event_id,event_id,event_key,symbol,lifecycle_type,
                 event_revision,payload,status)
            VALUES
                (@OutboxEventId,@EventId,@EventKey,@Symbol,@LifecycleType,
                 @Revision,CAST(@Payload AS JSON),'pending');
            """,
            new
            {
                OutboxEventId = outboxEventId, EventId = state.Id, state.EventKey,
                source.Symbol, LifecycleType = toStage, Revision = state.EventRevision + 1,
                Payload = payload
            }, transaction, cancellationToken: cancellationToken));
    }

    private async Task RefreshEventAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        BarLifecycleEventV2 source,
        LiveEventState state,
        CancellationToken cancellationToken)
    {
        var counts = await connection.QuerySingleAsync<HitCounts>(new CommandDefinition(
            """
            SELECT COUNT(*) TotalHitCount,
                   SUM(is_promotion=TRUE) ConfirmedHitCount,
                   SUM(status='CANDIDATE') PendingHitCount,
                   SUM(pair_kind='ROUND_00') Round00HitCount,
                   SUM(pair_kind='DOUBLE_DIGIT') DoubleDigitHitCount
            FROM pair_trend_live_hit WHERE event_id=@Id;
            """, new { state.Id }, transaction, cancellationToken: cancellationToken));
        // 失效后仍保留它失效前到达过的最高周期，便于复盘“观察/重点/成立后失效”。
        var strongest = state.Stage == "INVALIDATED" ? state.StrongestFrequency : StrongestFrequency(state.Stage);
        var frequencies = state.Stage == "INVALIDATED" ? state.Frequencies : Frequencies(state.Stage);
        var mask = state.Stage == "INVALIDATED" ? state.TimeframeMask : TimeframeMask(state.Stage);
        var score = state.Stage == "INVALIDATED" ? state.Score : StageScore(state.Stage);
        var summary = JsonSerializer.Serialize(new
        {
            state.EventKey, source.Symbol, state.PivotType, stage = state.Stage,
            isActive = state.IsActive, pairPrice = state.LatestPairPrice, state.PriceTicks,
            state.Generation, frequencies, strongestFrequency = strongest,
            counts.TotalHitCount, counts.ConfirmedHitCount, score,
            algorithmVersion = _options.AlgorithmVersion
        }, JsonOptions);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_live_event SET
                timeframe_mask=@Mask,frequencies=@Frequencies,
                strongest_frequency=@Strongest,confluence_count=@ConfluenceCount,
                total_hit_count=@TotalHitCount,confirmed_hit_count=@ConfirmedHitCount,
                invalidated_hit_count=IF(stage='INVALIDATED',1,0),
                pending_hit_count=@PendingHitCount,round_00_hit_count=@Round00HitCount,
                double_digit_hit_count=@DoubleDigitHitCount,score=@Score,
                event_revision=event_revision+1,content_hash=@ContentHash,
                summary_json=CAST(@SummaryJson AS JSON),last_source_event_id=@SourceEventId
            WHERE id=@Id;
            """,
            new
            {
                state.Id, Mask = mask, Frequencies = frequencies, Strongest = strongest,
                ConfluenceCount = mask == 15 ? 4 : mask == 7 ? 3 : mask == 3 ? 2 : 1,
                counts.TotalHitCount, counts.ConfirmedHitCount,
                PendingHitCount = state.Stage == "DISCOVERED" ? counts.TotalHitCount : 0,
                counts.Round00HitCount, counts.DoubleDigitHitCount, Score = score,
                ContentHash = Hash(summary), SummaryJson = summary,
                SourceEventId = source.EventId
            }, transaction, cancellationToken: cancellationToken));
        state.EventRevision++;
        state.TimeframeMask = mask;
        state.Frequencies = frequencies;
        state.StrongestFrequency = strongest;
        state.Score = score;
    }

    private static async Task EnsureAndLockCheckpointAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        BarLifecycleEventV2 source,
        int shard,
        string streamKey,
        string messageId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO pair_trend_consumer_checkpoint
                (shard,stream_key,last_message_id,last_source_event_id,last_success_at)
            VALUES (@Shard,@StreamKey,@MessageId,@EventId,CURRENT_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE shard=shard;
            """,
            new { Shard = shard, StreamKey = streamKey, MessageId = messageId, source.EventId },
            transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT shard FROM pair_trend_consumer_checkpoint WHERE shard=@Shard FOR UPDATE;",
            new { Shard = shard }, transaction, cancellationToken: cancellationToken));
    }

    private static DateTime EffectiveEob(BarLifecycleEventV2 value) =>
        value.Frequency == "1d"
            ? value.TradingDate.ToDateTime(new TimeOnly(15, 0))
            : value.Eob.DateTime;

    private static int StageRank(string stage) => stage switch
    {
        "DISCOVERED" => 1, "OBSERVING" => 2, "FOCUS" => 3,
        "ESTABLISHED" => 4, _ => 9
    };
    private static int TimeframeMask(string stage) => stage switch
    {
        "OBSERVING" => 3, "FOCUS" => 7, "ESTABLISHED" => 15,
        "INVALIDATED" => 1, _ => 1
    };
    private static string Frequencies(string stage) => stage switch
    {
        "OBSERVING" => "5m,30m", "FOCUS" => "5m,30m,60m",
        "ESTABLISHED" => "5m,30m,60m,1d", _ => "5m"
    };
    private static string StrongestFrequency(string stage) => stage switch
    {
        "OBSERVING" => "30m", "FOCUS" => "60m", "ESTABLISHED" => "1d", _ => "5m"
    };
    private static decimal StageScore(string stage) => stage switch
    {
        "ESTABLISHED" => 1m, "FOCUS" => .75m, "OBSERVING" => .5m, _ => .25m
    };
    private static string Db(PairPivotType value) => value == PairPivotType.Top ? "TOP" : "BOTTOM";
    private static string Db(PairPriceKind value) => value == PairPriceKind.Round00 ? "ROUND_00" : "DOUBLE_DIGIT";
    private static string Hash(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class LiveEventState
    {
        public long Id { get; init; }
        public string EventKey { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string? SymbolName { get; init; }
        public string PivotType { get; init; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public long PriceTicks { get; init; }
        public decimal LatestPairPrice { get; init; }
        public int LatestPairCode { get; init; }
        public string LatestPairKind { get; init; } = string.Empty;
        public int Generation { get; init; }
        public DateTime DiscoveredAt { get; init; }
        public int EventRevision { get; set; }
        public int TimeframeMask { get; set; } = 1;
        public string Frequencies { get; set; } = "5m";
        public string StrongestFrequency { get; set; } = "5m";
        public decimal Score { get; set; } = .25m;
    }

    private sealed class HitCounts
    {
        public int TotalHitCount { get; init; }
        public int ConfirmedHitCount { get; init; }
        public int PendingHitCount { get; init; }
        public int Round00HitCount { get; init; }
        public int DoubleDigitHitCount { get; init; }
    }

    private const string SelectActiveEventsSql = """
        SELECT id Id,event_key EventKey,symbol Symbol,symbol_name SymbolName,
               pivot_type PivotType,stage Stage,is_active IsActive,price_ticks PriceTicks,
               latest_pair_price LatestPairPrice,latest_pair_code LatestPairCode,
               latest_pair_kind LatestPairKind,generation Generation,
               discovered_at DiscoveredAt,event_revision EventRevision,
               timeframe_mask TimeframeMask,frequencies Frequencies,
               strongest_frequency StrongestFrequency,score Score
        FROM pair_trend_live_event
        WHERE symbol=@Symbol AND algorithm_version=@AlgorithmVersion AND is_active=TRUE
        ORDER BY id FOR UPDATE;
        """;

    private const string InsertEventSql = """
        INSERT INTO pair_trend_live_event
            (event_key,symbol,symbol_name,pivot_type,status,first_seen_at,last_seen_at,
             latest_pair_price,price_ticks,latest_pair_code,latest_pair_kind,timeframe_mask,
             frequencies,strongest_frequency,confluence_count,total_hit_count,
             pending_hit_count,round_00_hit_count,double_digit_hit_count,score,
             max_trend_strength,algorithm_version,stage,generation,is_active,discovered_at,
             root_5m_bob,root_5m_eob,last_transition_at,event_revision,content_hash,
             last_source_event_id,summary_json)
        VALUES
            (@EventKey,@Symbol,@SymbolName,@PivotType,'CANDIDATE',@FirstSeenAt,@LastSeenAt,
             @LatestPairPrice,@PriceTicks,@LatestPairCode,@LatestPairKind,1,
             '5m','5m',1,0,0,0,0,0.25,0,@AlgorithmVersion,'DISCOVERED',@Generation,TRUE,
             @FirstSeenAt,@RootBob,@RootEob,@FirstSeenAt,0,@ContentHash,
             @LastSourceEventId,CAST(@SummaryJson AS JSON));
        """;

    private const string UpsertHitSql = """
        INSERT INTO pair_trend_live_hit
            (event_id,hit_key,symbol,frequency,trading_date,bob,eob,observed_at,confirmed_at,
             pivot_type,status,pair_price,price_ticks,pair_code,pair_kind,hit_field,
             trend_direction,trend_strength,ema20,ema60,atr14,previous_close,
             open_price,high_price,low_price,close_price,volume,amount,is_rolling_extreme,
             volume_percentile,wick_ratio,reversal_atr,score,confirmation_reason,
             source_revision,source_row_hash,source_event_id,algorithm_version,stage,
             is_promotion,details_json)
        VALUES
            (@EventId,@HitKey,@Symbol,@Frequency,@TradingDate,@Bob,@Eob,@ObservedAt,@ConfirmedAt,
             @PivotType,@Status,@PairPrice,@PriceTicks,@PairCode,@PairKind,@HitField,
             'UNKNOWN',0,0,0,0,@PreClose,@Open,@High,@Low,@Close,@Volume,@Amount,FALSE,
             0,0,0,0.25,@Reason,@Revision,@RowHash,@SourceEventId,@AlgorithmVersion,@Stage,
             @IsPromotion,CAST(@DetailsJson AS JSON))
        ON DUPLICATE KEY UPDATE
             event_id=VALUES(event_id),hit_key=VALUES(hit_key),status=VALUES(status),
             confirmed_at=VALUES(confirmed_at),pair_price=VALUES(pair_price),
             price_ticks=VALUES(price_ticks),pair_code=VALUES(pair_code),
             pair_kind=VALUES(pair_kind),previous_close=VALUES(previous_close),
             open_price=VALUES(open_price),high_price=VALUES(high_price),
             low_price=VALUES(low_price),close_price=VALUES(close_price),
             volume=VALUES(volume),amount=VALUES(amount),confirmation_reason=VALUES(confirmation_reason),
             source_revision=GREATEST(source_revision,VALUES(source_revision)),
             source_row_hash=VALUES(source_row_hash),source_event_id=VALUES(source_event_id),
             stage=VALUES(stage),is_promotion=VALUES(is_promotion),details_json=VALUES(details_json);
        """;
}
