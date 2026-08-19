using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Domain.Analytics;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;
using MySqlConnector;

namespace AStockMonitor.Infrastructure.Analytics;

/// <summary>
/// 将同一股票的内存回放快照幂等投影到实时对子表。
///
/// 每次写入只锁定一只股票、一个交易日的结果；不会删除重建，也不会让共享 Run 行成为
/// 并发热点。来源修订后已不再出现的事件会明确标为 SOURCE_RECONCILIATION 失效。
/// </summary>
public sealed class PairTrendLiveSnapshotWriter(IMySqlConnectionFactory connectionFactory)
    : IPairTrendLiveSnapshotWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(
        DateOnly tradingDate,
        string sourceCycleId,
        PairTrendSymbolResult result,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await WriteOnceAsync(tradingDate, sourceCycleId, result, cancellationToken);
                return;
            }
            catch (MySqlException exception) when (exception.Number is 1205 or 1213 && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(80 * attempt * attempt), cancellationToken);
            }
        }
    }

    private async Task WriteOnceAsync(
        DateOnly tradingDate,
        string sourceCycleId,
        PairTrendSymbolResult result,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        // 锁定当前股票当前交易日已有行，确保两个重复回传不会交错做 reconcile。
        await connection.QueryAsync<long>(new CommandDefinition(
            """
            SELECT id
            FROM pair_trend_live_event
            WHERE symbol=@Symbol AND algorithm_version=@AlgorithmVersion
              AND DATE(root_5m_eob)=@TradingDate
            FOR UPDATE;
            """,
            new
            {
                result.Symbol,
                AlgorithmVersion = PairTrendOptions.CurrentAlgorithmVersion,
                TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue)
            }, transaction, cancellationToken: cancellationToken));

        var currentEventKeys = result.Events
            .Select(static item => item.EventKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var pairEvent in result.Events)
        {
            var summaryJson = JsonSerializer.Serialize(new
            {
                pairEvent.Symbol,
                pivotType = Db(pairEvent.PivotType),
                status = Db(pairEvent.Status),
                pairEvent.Frequencies,
                pairEvent.ConfluenceCount,
                pairEvent.TotalHitCount,
                pairEvent.ConfirmedHitCount,
                pairEvent.InvalidatedHitCount,
                pairEvent.PendingHitCount,
                pairEvent.Round00HitCount,
                pairEvent.DoubleDigitHitCount,
                pairEvent.Score,
                stage = Db(pairEvent.Stage),
                pairEvent.PriceTicks,
                pairEvent.Generation,
                pairEvent.IsActive,
                pairEvent.ObservedAt,
                pairEvent.FocusedAt,
                pairEvent.EstablishedAt,
                pairEvent.InvalidatedAt,
                pairEvent.InvalidatedPrice,
                pairEvent.InvalidationReason
            }, JsonOptions);
            var contentHash = Hash(summaryJson);

            await connection.ExecuteAsync(new CommandDefinition(UpsertEventSql, new
            {
                pairEvent.EventKey,
                pairEvent.Symbol,
                pairEvent.SymbolName,
                PivotType = Db(pairEvent.PivotType),
                Status = Db(pairEvent.Status),
                pairEvent.FirstSeenAt,
                pairEvent.LastSeenAt,
                pairEvent.ConfirmedAt,
                pairEvent.LatestPairPrice,
                pairEvent.PriceTicks,
                pairEvent.LatestPairCode,
                LatestPairKind = Db(pairEvent.LatestPairKind),
                pairEvent.TimeframeMask,
                pairEvent.Frequencies,
                pairEvent.StrongestFrequency,
                pairEvent.ConfluenceCount,
                pairEvent.TotalHitCount,
                pairEvent.ConfirmedHitCount,
                pairEvent.InvalidatedHitCount,
                pairEvent.PendingHitCount,
                RetractedHitCount = 0,
                pairEvent.Round00HitCount,
                pairEvent.DoubleDigitHitCount,
                pairEvent.Score,
                pairEvent.MaxTrendStrength,
                pairEvent.AlgorithmVersion,
                Stage = Db(pairEvent.Stage),
                pairEvent.Generation,
                pairEvent.IsActive,
                DiscoveredAt = pairEvent.FirstSeenAt,
                pairEvent.ObservedAt,
                pairEvent.FocusedAt,
                pairEvent.EstablishedAt,
                pairEvent.InvalidatedAt,
                pairEvent.InvalidatedPrice,
                pairEvent.InvalidationReason,
                pairEvent.RootFiveMinuteBob,
                pairEvent.RootFiveMinuteEob,
                LastTransitionAt = pairEvent.Lifecycles?.Max(static item => item.OccurredAt)
                    ?? pairEvent.LastSeenAt,
                ContentHash = contentHash,
                LastSourceEventId = sourceCycleId,
                SummaryJson = summaryJson
            }, transaction, cancellationToken: cancellationToken));

            var eventId = await connection.QuerySingleAsync<long>(new CommandDefinition(
                "SELECT id FROM pair_trend_live_event WHERE event_key=@EventKey;",
                new { pairEvent.EventKey }, transaction, cancellationToken: cancellationToken));

            // Wave-bottom is supplementary and becomes eligible after an active
            // BOTTOM has reached FOCUS.  Keep ESTABLISHED eligible as well: its
            // focused_at is the immutable scoring anchor, so promotion must not
            // erase or prevent the original point-in-time signal.
            if (pairEvent.PivotType == PairPivotType.Bottom &&
                pairEvent.Stage is PairTrendStage.Focus or PairTrendStage.Established &&
                pairEvent.IsActive &&
                pairEvent.FocusedAt is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO wave_bottom_collection_job(
                        event_id,symbol,focused_at,data_end_date,required_daily_bars,
                        adjust_mode,algorithm_version,status)
                    VALUES(
                        @EventId,@Symbol,@FocusedAt,DATE_SUB(DATE(@FocusedAt),INTERVAL 1 DAY),
                        120,'NONE',@WaveAlgorithmVersion,'PENDING')
                    ON DUPLICATE KEY UPDATE
                        status=IF(focused_at<>VALUES(focused_at),'PENDING',status),
                        attempt_count=IF(focused_at<>VALUES(focused_at),0,attempt_count),
                        next_attempt_at=IF(focused_at<>VALUES(focused_at),NULL,next_attempt_at),
                        last_error=IF(focused_at<>VALUES(focused_at),NULL,last_error),
                        completed_at=IF(focused_at<>VALUES(focused_at),NULL,completed_at),
                        lease_token=IF(focused_at<>VALUES(focused_at),NULL,lease_token),
                        lease_owner=IF(focused_at<>VALUES(focused_at),NULL,lease_owner),
                        lease_expires_at=IF(focused_at<>VALUES(focused_at),NULL,lease_expires_at),
                        focused_at=VALUES(focused_at),data_end_date=VALUES(data_end_date);

                    UPDATE pair_trend_live_event event
                    JOIN wave_bottom_collection_job job
                      ON job.event_id=event.id AND job.algorithm_version=@WaveAlgorithmVersion
                    SET event.wave_calculation_status=CASE
                        WHEN job.status='FAILED' THEN 'FAILED'
                        WHEN job.status='COMPLETED' THEN event.wave_calculation_status
                        ELSE 'PENDING'
                    END
                    WHERE event.id=@EventId;
                    """,
                    new
                    {
                        EventId = eventId,
                        pairEvent.Symbol,
                        FocusedAt = pairEvent.FocusedAt.Value,
                        WaveAlgorithmVersion = WaveBottomOptions.CurrentAlgorithmVersion
                    }, transaction, cancellationToken: cancellationToken));
            }

            foreach (var hit in pairEvent.Hits)
            {
                var detailsJson = JsonSerializer.Serialize(new
                {
                    hit.PriceTicks,
                    stage = Db(hit.Stage),
                    hit.IsPromotion
                }, JsonOptions);
                await connection.ExecuteAsync(new CommandDefinition(UpsertHitSql, new
                {
                    EventId = eventId,
                    hit.HitKey,
                    hit.Symbol,
                    hit.Frequency,
                    hit.TradingDate,
                    hit.Bob,
                    hit.Eob,
                    hit.ObservedAt,
                    hit.ConfirmedAt,
                    PivotType = Db(hit.PivotType),
                    Status = Db(hit.Status),
                    hit.PairPrice,
                    hit.PriceTicks,
                    hit.PairCode,
                    PairKind = Db(hit.PairKind),
                    hit.HitField,
                    TrendDirection = Db(hit.TrendDirection),
                    hit.TrendStrength,
                    hit.Ema20,
                    hit.Ema60,
                    hit.Atr14,
                    PreviousClose = hit.PreviousClose,
                    hit.OpenPrice,
                    hit.HighPrice,
                    hit.LowPrice,
                    hit.ClosePrice,
                    Volume = Math.Max(0, hit.Volume),
                    hit.Amount,
                    hit.IsRollingExtreme,
                    hit.VolumePercentile,
                    hit.WickRatio,
                    hit.ReversalAtr,
                    hit.Score,
                    hit.ConfirmationReason,
                    SourceRevision = 0,
                    hit.SourceRowHash,
                    SourceEventId = sourceCycleId,
                    hit.AlgorithmVersion,
                    Stage = Db(hit.Stage),
                    hit.IsPromotion,
                    DetailsJson = detailsJson
                }, transaction, cancellationToken: cancellationToken));
            }

            if (pairEvent.Lifecycles is not { Count: > 0 })
                continue;
            foreach (var lifecycle in pairEvent.Lifecycles)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT IGNORE INTO pair_trend_live_lifecycle
                        (event_id,lifecycle_key,symbol,from_stage,to_stage,occurred_at,
                         trigger_frequency,trigger_price,reason,source_row_hash,should_notify)
                    VALUES
                        (@EventId,@LifecycleKey,@Symbol,@FromStage,@ToStage,@OccurredAt,
                         @TriggerFrequency,@TriggerPrice,@Reason,@SourceRowHash,@ShouldNotify);
                    """,
                    new
                    {
                        EventId = eventId,
                        lifecycle.LifecycleKey,
                        Symbol = pairEvent.Symbol,
                        FromStage = lifecycle.FromStage is null ? null : Db(lifecycle.FromStage.Value),
                        ToStage = Db(lifecycle.ToStage),
                        lifecycle.OccurredAt,
                        lifecycle.TriggerFrequency,
                        lifecycle.TriggerPrice,
                        lifecycle.Reason,
                        lifecycle.SourceRowHash,
                        lifecycle.ShouldNotify
                    }, transaction, cancellationToken: cancellationToken));
            }
        }

        // 供应商修订使旧发现点消失时，不保留“幽灵活动事件”。不删审计记录，明确失效。
        if (currentEventKeys.Count == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE pair_trend_live_event
                SET status='INVALIDATED',stage='INVALIDATED',is_active=FALSE,
                    invalidated_at=COALESCE(invalidated_at,CURRENT_TIMESTAMP(6)),
                    invalidation_reason=COALESCE(invalidation_reason,'SOURCE_RECONCILIATION'),
                    last_source_event_id=@SourceCycleId
                WHERE symbol=@Symbol AND algorithm_version=@AlgorithmVersion
                  AND DATE(root_5m_eob)=@TradingDate AND is_active=TRUE;
                """,
                new
                {
                    SourceCycleId = sourceCycleId,
                    result.Symbol,
                    AlgorithmVersion = PairTrendOptions.CurrentAlgorithmVersion,
                    TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue)
                }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE pair_trend_live_event
                SET status='INVALIDATED',stage='INVALIDATED',is_active=FALSE,
                    invalidated_at=COALESCE(invalidated_at,CURRENT_TIMESTAMP(6)),
                    invalidation_reason=COALESCE(invalidation_reason,'SOURCE_RECONCILIATION'),
                    last_source_event_id=@SourceCycleId
                WHERE symbol=@Symbol AND algorithm_version=@AlgorithmVersion
                  AND DATE(root_5m_eob)=@TradingDate AND is_active=TRUE
                  AND event_key NOT IN @EventKeys;
                """,
                new
                {
                    SourceCycleId = sourceCycleId,
                    result.Symbol,
                    AlgorithmVersion = PairTrendOptions.CurrentAlgorithmVersion,
                    TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue),
                    EventKeys = currentEventKeys.ToArray()
                }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string Db(PairPivotType value) => value == PairPivotType.Top ? "TOP" : "BOTTOM";
    private static string Db(PairEventStatus value) => value.ToString().ToUpperInvariant();
    private static string Db(PairHitStatus value) => value.ToString().ToUpperInvariant();
    private static string Db(PairTrendDirection value) => value.ToString().ToUpperInvariant();
    private static string Db(PairPriceKind value) => value == PairPriceKind.Round00 ? "ROUND_00" : "DOUBLE_DIGIT";
    private static string Db(PairTrendStage value) => value switch
    {
        PairTrendStage.Discovered => "DISCOVERED",
        PairTrendStage.Observing => "OBSERVING",
        PairTrendStage.Focus => "FOCUS",
        PairTrendStage.Established => "ESTABLISHED",
        PairTrendStage.Invalidated => "INVALIDATED",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string Hash(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private const string UpsertEventSql = """
        INSERT INTO pair_trend_live_event
            (event_key,symbol,symbol_name,pivot_type,status,first_seen_at,last_seen_at,
             confirmed_at,latest_pair_price,price_ticks,latest_pair_code,latest_pair_kind,
             timeframe_mask,frequencies,strongest_frequency,confluence_count,total_hit_count,
             confirmed_hit_count,invalidated_hit_count,pending_hit_count,retracted_hit_count,
             round_00_hit_count,double_digit_hit_count,score,max_trend_strength,
             algorithm_version,stage,generation,is_active,discovered_at,observed_at,focused_at,
             established_at,invalidated_at,invalidated_price,invalidation_reason,root_5m_bob,
             root_5m_eob,last_transition_at,event_revision,content_hash,last_source_event_id,summary_json)
        VALUES
            (@EventKey,@Symbol,@SymbolName,@PivotType,@Status,@FirstSeenAt,@LastSeenAt,
             @ConfirmedAt,@LatestPairPrice,@PriceTicks,@LatestPairCode,@LatestPairKind,
             @TimeframeMask,@Frequencies,@StrongestFrequency,@ConfluenceCount,@TotalHitCount,
             @ConfirmedHitCount,@InvalidatedHitCount,@PendingHitCount,@RetractedHitCount,
             @Round00HitCount,@DoubleDigitHitCount,@Score,@MaxTrendStrength,
             @AlgorithmVersion,@Stage,@Generation,@IsActive,@DiscoveredAt,@ObservedAt,@FocusedAt,
             @EstablishedAt,@InvalidatedAt,@InvalidatedPrice,@InvalidationReason,@RootFiveMinuteBob,
             @RootFiveMinuteEob,@LastTransitionAt,0,@ContentHash,@LastSourceEventId,@SummaryJson)
        ON DUPLICATE KEY UPDATE
            symbol_name=VALUES(symbol_name),status=VALUES(status),first_seen_at=VALUES(first_seen_at),
            last_seen_at=VALUES(last_seen_at),confirmed_at=VALUES(confirmed_at),
            latest_pair_price=VALUES(latest_pair_price),price_ticks=VALUES(price_ticks),
            latest_pair_code=VALUES(latest_pair_code),latest_pair_kind=VALUES(latest_pair_kind),
            timeframe_mask=VALUES(timeframe_mask),frequencies=VALUES(frequencies),
            strongest_frequency=VALUES(strongest_frequency),confluence_count=VALUES(confluence_count),
            total_hit_count=VALUES(total_hit_count),confirmed_hit_count=VALUES(confirmed_hit_count),
            invalidated_hit_count=VALUES(invalidated_hit_count),pending_hit_count=VALUES(pending_hit_count),
            retracted_hit_count=VALUES(retracted_hit_count),round_00_hit_count=VALUES(round_00_hit_count),
            double_digit_hit_count=VALUES(double_digit_hit_count),score=VALUES(score),
            max_trend_strength=VALUES(max_trend_strength),stage=VALUES(stage),generation=VALUES(generation),
            is_active=VALUES(is_active),discovered_at=VALUES(discovered_at),observed_at=VALUES(observed_at),
            focused_at=VALUES(focused_at),established_at=VALUES(established_at),
            invalidated_at=VALUES(invalidated_at),invalidated_price=VALUES(invalidated_price),
            invalidation_reason=VALUES(invalidation_reason),root_5m_bob=VALUES(root_5m_bob),
            root_5m_eob=VALUES(root_5m_eob),last_transition_at=VALUES(last_transition_at),
            event_revision=IF(content_hash<>VALUES(content_hash),event_revision+1,event_revision),
            content_hash=VALUES(content_hash),last_source_event_id=VALUES(last_source_event_id),
            summary_json=VALUES(summary_json);
        """;

    private const string UpsertHitSql = """
        INSERT INTO pair_trend_live_hit
            (event_id,hit_key,symbol,frequency,trading_date,bob,eob,observed_at,confirmed_at,
             pivot_type,status,pair_price,price_ticks,pair_code,pair_kind,hit_field,
             trend_direction,trend_strength,ema20,ema60,atr14,previous_close,open_price,
             high_price,low_price,close_price,volume,amount,is_rolling_extreme,volume_percentile,
             wick_ratio,reversal_atr,score,confirmation_reason,source_revision,source_row_hash,
             source_event_id,algorithm_version,stage,is_promotion,details_json)
        VALUES
            (@EventId,@HitKey,@Symbol,@Frequency,@TradingDate,@Bob,@Eob,@ObservedAt,@ConfirmedAt,
             @PivotType,@Status,@PairPrice,@PriceTicks,@PairCode,@PairKind,@HitField,
             @TrendDirection,@TrendStrength,@Ema20,@Ema60,@Atr14,@PreviousClose,@OpenPrice,
             @HighPrice,@LowPrice,@ClosePrice,@Volume,@Amount,@IsRollingExtreme,@VolumePercentile,
             @WickRatio,@ReversalAtr,@Score,@ConfirmationReason,@SourceRevision,@SourceRowHash,
             @SourceEventId,@AlgorithmVersion,@Stage,@IsPromotion,@DetailsJson)
        ON DUPLICATE KEY UPDATE
            event_id=VALUES(event_id),hit_key=VALUES(hit_key),trading_date=VALUES(trading_date),
            bob=VALUES(bob),observed_at=VALUES(observed_at),confirmed_at=VALUES(confirmed_at),
            status=VALUES(status),pair_price=VALUES(pair_price),price_ticks=VALUES(price_ticks),
            pair_code=VALUES(pair_code),pair_kind=VALUES(pair_kind),hit_field=VALUES(hit_field),
            trend_direction=VALUES(trend_direction),trend_strength=VALUES(trend_strength),ema20=VALUES(ema20),
            ema60=VALUES(ema60),atr14=VALUES(atr14),previous_close=VALUES(previous_close),
            open_price=VALUES(open_price),high_price=VALUES(high_price),low_price=VALUES(low_price),
            close_price=VALUES(close_price),volume=VALUES(volume),amount=VALUES(amount),
            is_rolling_extreme=VALUES(is_rolling_extreme),volume_percentile=VALUES(volume_percentile),
            wick_ratio=VALUES(wick_ratio),reversal_atr=VALUES(reversal_atr),score=VALUES(score),
            confirmation_reason=VALUES(confirmation_reason),source_revision=VALUES(source_revision),
            source_row_hash=VALUES(source_row_hash),source_event_id=VALUES(source_event_id),
            stage=VALUES(stage),is_promotion=VALUES(is_promotion),details_json=VALUES(details_json);
        """;
}
