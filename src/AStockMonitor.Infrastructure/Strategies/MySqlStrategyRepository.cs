using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AStockMonitor.Application.Strategies;
using AStockMonitor.Domain.Strategies;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Infrastructure.Strategies;

/// <summary>使用不可变事件和当日机会投影持久化策略结果。</summary>
public sealed class MySqlStrategyRepository(IMySqlConnectionFactory connectionFactory) : IStrategyRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlySet<string>> GetEnabledStrategyCodesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT strategy_code FROM strategy_definition WHERE enabled=TRUE;",
            cancellationToken: cancellationToken));
        return rows.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<StrategyScanRun?> TryStartRunAsync(
        string runKey,
        StrategyScanProfile profile,
        string triggerType,
        DateOnly tradingDate,
        CancellationToken cancellationToken = default)
    {
        const string insert = """
            INSERT IGNORE INTO strategy_scan_run
                (run_key, scan_profile, trigger_type, trading_date, status, started_at)
            VALUES (@RunKey, @Profile, @TriggerType, @TradingDate, 'running', UTC_TIMESTAMP(6));
            """;
        const string select = """
            SELECT id Id, run_key RunKey, scan_profile Profile, trigger_type TriggerType,
                   trading_date TradingDate, started_at StartedAt, status Status
            FROM strategy_scan_run WHERE run_key=@RunKey;
            """;
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(insert, new
        {
            RunKey = runKey,
            Profile = profile.ToString().ToLowerInvariant(),
            TriggerType = triggerType,
            TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue)
        }, cancellationToken: cancellationToken));
        if (inserted == 0) return null;
        var row = await connection.QuerySingleAsync<RunRow>(new CommandDefinition(
            select, new { RunKey = runKey }, cancellationToken: cancellationToken));
        return row.ToDomain();
    }

    public async Task PersistEvaluationsAsync(
        StrategyScanRun run,
        IReadOnlyCollection<StrategyEvaluation> evaluations,
        CancellationToken cancellationToken = default)
    {
        var qualified = evaluations.Where(static x => x.Qualified).ToArray();
        if (qualified.Length == 0) return;
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var evaluation in qualified)
        {
            var date = evaluation.TradingDate.ToDateTime(TimeOnly.MinValue);
            var prior = await connection.QuerySingleOrDefaultAsync<PriorDetailRow>(new CommandDefinition(
                """
                SELECT d.current_score CurrentScore, d.action Action, d.latest_event_id LatestEventId,
                       d.source_watermark SourceWatermark, d.last_seen_at LastSeenAt
                FROM strategy_opportunity o
                JOIN strategy_opportunity_detail d ON d.opportunity_id=o.id
                WHERE o.trading_date=@Date AND o.symbol=@Symbol AND d.strategy_code=@StrategyCode
                FOR UPDATE;
                """,
                new { Date = date, evaluation.Symbol, evaluation.StrategyCode }, transaction,
                cancellationToken: cancellationToken));

            var eventType = DetermineEventType(prior, evaluation);
            var eventId = Hash(string.Join('|', evaluation.StrategyCode, evaluation.StrategyVersion,
                evaluation.Symbol, evaluation.TradingDate.ToString("yyyyMMdd"),
                evaluation.ObservedAt.UtcTicks, evaluation.Action, evaluation.Score,
                evaluation.SourceWatermark, eventType));
            var hitPrice = ReadPrice(evaluation.FeatureJson);
            var eventPayload = JsonSerializer.Serialize(new
            {
                eventId,
                previousEventId = prior?.LatestEventId,
                eventType = eventType.ToString(),
                evaluation.StrategyCode,
                evaluation.StrategyVersion,
                evaluation.Symbol,
                tradingDate = evaluation.TradingDate,
                evaluation.ObservedAt,
                action = evaluation.Action.ToString(),
                confidence = evaluation.Confidence.ToString(),
                evaluation.Score,
                hitPrice,
                evaluation.StopReference,
                evaluation.TargetReference,
                evaluation.PassedConditions,
                evaluation.FailedConditions,
                evaluation.SourceWatermark
            }, JsonOptions);

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO strategy_signal_event
                (event_id, previous_event_id, run_id, strategy_code, strategy_version,
                 symbol, trading_date, observed_at, event_type, action, confidence, score,
                 hit_price, stop_reference, target_reference, passed_conditions, failed_conditions,
                 feature_snapshot, parameter_snapshot, source_watermark)
                VALUES
                (@EventId, @PreviousEventId, @RunId, @StrategyCode, @StrategyVersion,
                 @Symbol, @TradingDate, @ObservedAt, @EventType, @Action, @Confidence, @Score,
                 @HitPrice, @StopReference, @TargetReference, CAST(@Passed AS JSON), CAST(@Failed AS JSON),
                 CAST(@Features AS JSON), CAST(@Parameters AS JSON), @SourceWatermark)
                ON DUPLICATE KEY UPDATE event_id=event_id;

                INSERT INTO strategy_event_outbox (event_id, payload)
                VALUES (@EventId, CAST(@Payload AS JSON))
                ON DUPLICATE KEY UPDATE event_id=event_id;
                """, new
                {
                    EventId = eventId,
                    PreviousEventId = prior?.LatestEventId,
                    RunId = run.Id,
                    evaluation.StrategyCode,
                    evaluation.StrategyVersion,
                    evaluation.Symbol,
                    TradingDate = date,
                    ObservedAt = evaluation.ObservedAt.UtcDateTime,
                    EventType = eventType.ToString().ToLowerInvariant(),
                    Action = evaluation.Action.ToString().ToLowerInvariant(),
                    Confidence = evaluation.Confidence.ToString().ToLowerInvariant(),
                    evaluation.Score,
                    HitPrice = hitPrice,
                    evaluation.StopReference,
                    evaluation.TargetReference,
                    Passed = JsonSerializer.Serialize(evaluation.PassedConditions, JsonOptions),
                    Failed = JsonSerializer.Serialize(evaluation.FailedConditions, JsonOptions),
                    Features = evaluation.FeatureJson,
                    Parameters = evaluation.ParameterJson,
                    evaluation.SourceWatermark,
                    Payload = eventPayload
                }, transaction, cancellationToken: cancellationToken));

            await UpsertOpportunityAsync(connection, transaction, evaluation, eventId, hitPrice,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteRunAsync(
        long runId, int requestedSymbols, int completedSymbols, int qualifiedSignals,
        string status, string? error, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE strategy_scan_run
            SET status=@Status, requested_symbols=@RequestedSymbols,
                completed_symbols=@CompletedSymbols, qualified_signals=@QualifiedSignals,
                error_message=@Error, finished_at=UTC_TIMESTAMP(6)
            WHERE id=@RunId;
            """;
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            RunId = runId, RequestedSymbols = requestedSymbols, CompletedSymbols = completedSymbols,
            QualifiedSignals = qualifiedSignals, Status = status, Error = error
        }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<StrategyOutboxMessage>> ClaimOutboxAsync(
        int limit, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id Id, event_id EventId, CAST(payload AS CHAR) Payload
            FROM strategy_event_outbox
            WHERE published_at IS NULL
            ORDER BY id LIMIT @Limit;
            """;
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<StrategyOutboxMessage>(new CommandDefinition(
            sql, new { Limit = Math.Clamp(limit, 1, 1000) }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task MarkOutboxPublishedAsync(
        IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return;
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE strategy_event_outbox SET published_at=UTC_TIMESTAMP(6), attempt_count=attempt_count+1 WHERE id IN @Ids;",
            new { Ids = ids }, cancellationToken: cancellationToken));
    }

    public async Task<int> ApplyLifecycleAsync(
        StrategyScanRun run,
        DateTimeOffset now,
        TimeSpan weakenAfter,
        TimeSpan expireAfter,
        CancellationToken cancellationToken = default)
    {
        const string candidatesSql = """
            SELECT o.id OpportunityId, o.symbol Symbol, d.strategy_code StrategyCode,
                   d.strategy_version StrategyVersion, d.action Action, d.confidence Confidence,
                   d.current_score Score, d.latest_event_id LatestEventId,
                   d.source_watermark SourceWatermark, d.last_seen_at LastSeenAt, d.status Status
            FROM strategy_opportunity o
            JOIN strategy_opportunity_detail d ON d.opportunity_id=o.id
            WHERE o.trading_date=@TradingDate
              AND d.status IN ('active','weakened')
              AND d.last_seen_at<=@WeakenBefore
            FOR UPDATE;
            """;
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<LifecycleRow>(new CommandDefinition(
            candidatesSql,
            new
            {
                TradingDate = run.TradingDate.ToDateTime(TimeOnly.MinValue),
                WeakenBefore = now.Subtract(weakenAfter).UtcDateTime
            }, transaction, cancellationToken: cancellationToken))).AsList();
        var changed = 0;
        foreach (var row in rows)
        {
            var expired = row.LastSeenAt <= now.Subtract(expireAfter).UtcDateTime;
            var eventType = expired ? StrategySignalEventType.Expired : StrategySignalEventType.Weakened;
            var newStatus = expired ? "expired" : "weakened";
            if (string.Equals(row.Status, newStatus, StringComparison.OrdinalIgnoreCase)) continue;
            var eventId = Hash($"lifecycle|{row.LatestEventId}|{eventType}|{now.ToUnixTimeSeconds() / 60}");
            var payload = JsonSerializer.Serialize(new
            {
                eventId,
                previousEventId = row.LatestEventId,
                eventType = eventType.ToString(),
                row.StrategyCode,
                row.StrategyVersion,
                row.Symbol,
                tradingDate = run.TradingDate,
                observedAt = now,
                row.Action,
                row.Confidence,
                row.Score,
                row.SourceWatermark
            }, JsonOptions);
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO strategy_signal_event
                (event_id, previous_event_id, run_id, strategy_code, strategy_version,
                 symbol, trading_date, observed_at, event_type, action, confidence, score,
                 hit_price, stop_reference, target_reference, passed_conditions, failed_conditions,
                 feature_snapshot, parameter_snapshot, source_watermark)
                SELECT @EventId, e.event_id, @RunId, e.strategy_code, e.strategy_version,
                       e.symbol, e.trading_date, @ObservedAt, @EventType, e.action, e.confidence,
                       e.score, e.hit_price, e.stop_reference, e.target_reference,
                       e.passed_conditions, e.failed_conditions, e.feature_snapshot,
                       e.parameter_snapshot, e.source_watermark
                FROM strategy_signal_event e WHERE e.event_id=@PreviousEventId
                ON DUPLICATE KEY UPDATE event_id=event_id;

                INSERT INTO strategy_event_outbox (event_id, payload)
                VALUES (@EventId, CAST(@Payload AS JSON))
                ON DUPLICATE KEY UPDATE event_id=event_id;

                UPDATE strategy_opportunity_detail
                SET status=@NewStatus, latest_event_id=@EventId
                WHERE opportunity_id=@OpportunityId AND strategy_code=@StrategyCode;
                """, new
                {
                    EventId = eventId,
                    PreviousEventId = row.LatestEventId,
                    RunId = run.Id,
                    ObservedAt = now.UtcDateTime,
                    EventType = eventType.ToString().ToLowerInvariant(),
                    Payload = payload,
                    NewStatus = newStatus,
                    row.OpportunityId,
                    row.StrategyCode
                }, transaction, cancellationToken: cancellationToken));
            changed++;
        }

        if (rows.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE strategy_opportunity o
                SET o.status=CASE
                        WHEN EXISTS(SELECT 1 FROM strategy_opportunity_detail d WHERE d.opportunity_id=o.id AND d.status='active') THEN 'active'
                        WHEN EXISTS(SELECT 1 FROM strategy_opportunity_detail d WHERE d.opportunity_id=o.id AND d.status='weakened') THEN 'weakened'
                        ELSE 'expired' END,
                    o.weakened_at=CASE
                        WHEN NOT EXISTS(SELECT 1 FROM strategy_opportunity_detail d WHERE d.opportunity_id=o.id AND d.status='active')
                        THEN COALESCE(o.weakened_at, @Now) ELSE NULL END,
                    o.expired_at=CASE
                        WHEN NOT EXISTS(SELECT 1 FROM strategy_opportunity_detail d WHERE d.opportunity_id=o.id AND d.status<>'expired')
                        THEN COALESCE(o.expired_at, @Now) ELSE NULL END
                WHERE o.trading_date=@TradingDate;
                """, new
                {
                    TradingDate = run.TradingDate.ToDateTime(TimeOnly.MinValue),
                    Now = now.UtcDateTime
                }, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private static async Task UpsertOpportunityAsync(
        MySqlConnector.MySqlConnection connection,
        MySqlConnector.MySqlTransaction transaction,
        StrategyEvaluation evaluation,
        string eventId,
        decimal hitPrice,
        CancellationToken cancellationToken)
    {
        const string upsertOpportunity = """
            INSERT INTO strategy_opportunity
            (trading_date, symbol, level, status, primary_strategy_code, highest_score,
             strategy_count, first_seen_at, last_seen_at, latest_event_id)
            VALUES (@TradingDate, @Symbol, 'observe', 'active', @StrategyCode, @Score,
                    1, @ObservedAt, @ObservedAt, @EventId)
            ON DUPLICATE KEY UPDATE
                status='active', expired_at=NULL, weakened_at=NULL,
                highest_score=GREATEST(highest_score, VALUES(highest_score)),
                primary_strategy_code=IF(VALUES(highest_score)>=highest_score, VALUES(primary_strategy_code), primary_strategy_code),
                last_seen_at=GREATEST(last_seen_at, VALUES(last_seen_at)), latest_event_id=VALUES(latest_event_id);
            """;
        const string upsertDetail = """
            INSERT INTO strategy_opportunity_detail
            (opportunity_id, strategy_code, strategy_version, action, confidence,
             current_score, highest_score, hit_count, first_seen_at, last_seen_at,
             latest_event_id, source_watermark, status)
            VALUES (@OpportunityId, @StrategyCode, @StrategyVersion, @Action, @Confidence,
                    @Score, @Score, 1, @ObservedAt, @ObservedAt, @EventId, @SourceWatermark, 'active')
            ON DUPLICATE KEY UPDATE
                strategy_version=VALUES(strategy_version), action=VALUES(action), confidence=VALUES(confidence),
                current_score=VALUES(current_score), highest_score=GREATEST(highest_score, VALUES(highest_score)),
                hit_count=hit_count+1, last_seen_at=GREATEST(last_seen_at, VALUES(last_seen_at)),
                latest_event_id=VALUES(latest_event_id), source_watermark=VALUES(source_watermark), status='active';

            UPDATE strategy_opportunity o
            SET o.strategy_count=(SELECT COUNT(*) FROM strategy_opportunity_detail d WHERE d.opportunity_id=o.id AND d.status='active'),
                o.highest_score=(SELECT MAX(current_score) FROM strategy_opportunity_detail d WHERE d.opportunity_id=o.id AND d.status='active'),
                o.primary_strategy_code=(SELECT strategy_code FROM strategy_opportunity_detail d WHERE d.opportunity_id=o.id AND d.status='active' ORDER BY current_score DESC, strategy_code LIMIT 1),
                o.level=CASE
                    WHEN (SELECT COUNT(*) FROM strategy_opportunity_detail d WHERE d.opportunity_id=o.id AND d.status='active')>=2
                         AND (SELECT MAX(current_score) FROM strategy_opportunity_detail d WHERE d.opportunity_id=o.id AND d.status='active')>=85 THEN 'focus'
                    WHEN (SELECT MAX(current_score) FROM strategy_opportunity_detail d WHERE d.opportunity_id=o.id AND d.status='active')>=95 THEN 'focus'
                    WHEN (SELECT COUNT(*) FROM strategy_opportunity_detail d WHERE d.opportunity_id=o.id AND d.status='active')>=2
                         OR (SELECT MAX(current_score) FROM strategy_opportunity_detail d WHERE d.opportunity_id=o.id AND d.status='active')>=90 THEN 'candidate'
                    ELSE 'observe' END
            WHERE o.id=@OpportunityId;
            """;
        var parameters = new
        {
            TradingDate = evaluation.TradingDate.ToDateTime(TimeOnly.MinValue),
            evaluation.Symbol,
            evaluation.StrategyCode,
            evaluation.StrategyVersion,
            Action = evaluation.Action.ToString().ToLowerInvariant(),
            Confidence = evaluation.Confidence.ToString().ToLowerInvariant(),
            evaluation.Score,
            ObservedAt = evaluation.ObservedAt.UtcDateTime,
            EventId = eventId,
            evaluation.SourceWatermark,
            HitPrice = hitPrice
        };
        await connection.ExecuteAsync(new CommandDefinition(
            upsertOpportunity, parameters, transaction, cancellationToken: cancellationToken));
        var opportunityId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT id FROM strategy_opportunity WHERE trading_date=@TradingDate AND symbol=@Symbol;",
            parameters, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            upsertDetail,
            new
            {
                OpportunityId = opportunityId,
                parameters.TradingDate,
                parameters.Symbol,
                parameters.StrategyCode,
                parameters.StrategyVersion,
                parameters.Action,
                parameters.Confidence,
                parameters.Score,
                parameters.ObservedAt,
                parameters.EventId,
                parameters.SourceWatermark,
                parameters.HitPrice
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static StrategySignalEventType DetermineEventType(
        PriorDetailRow? prior, StrategyEvaluation evaluation)
    {
        if (prior is null) return StrategySignalEventType.New;
        if (!string.Equals(prior.SourceWatermark, evaluation.SourceWatermark, StringComparison.Ordinal) &&
            evaluation.ObservedAt <= new DateTimeOffset(DateTime.SpecifyKind(prior.LastSeenAt, DateTimeKind.Utc)).AddSeconds(5))
            return StrategySignalEventType.Revised;
        if (evaluation.Score >= prior.CurrentScore + 2m ||
            ActionRank(evaluation.Action) > ActionRank(prior.Action))
            return StrategySignalEventType.Strengthened;
        return StrategySignalEventType.Repeated;
    }

    private static int ActionRank(StrategyAction action) => action switch
    {
        StrategyAction.Confirm => 4,
        StrategyAction.Candidate => 3,
        StrategyAction.PullbackWait => 2,
        StrategyAction.Watch => 1,
        _ => 0
    };

    private static int ActionRank(string action) => action.ToLowerInvariant() switch
    {
        "confirm" => 4, "candidate" => 3, "pullbackwait" => 2, "watch" => 1, _ => 0
    };

    private static decimal ReadPrice(string featureJson)
    {
        using var document = JsonDocument.Parse(featureJson);
        return document.RootElement.TryGetProperty("price", out var price) ? price.GetDecimal() : 0m;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RunRow
    {
        public long Id { get; init; }
        public string RunKey { get; init; } = string.Empty;
        public string Profile { get; init; } = string.Empty;
        public string TriggerType { get; init; } = string.Empty;
        public DateTime TradingDate { get; init; }
        public DateTime StartedAt { get; init; }
        public StrategyScanRun ToDomain() => new(Id, RunKey,
            Enum.Parse<StrategyScanProfile>(Profile, true), TriggerType,
            DateOnly.FromDateTime(TradingDate), new DateTimeOffset(DateTime.SpecifyKind(StartedAt, DateTimeKind.Utc)));
    }

    private sealed class PriorDetailRow
    {
        public decimal CurrentScore { get; init; }
        public string Action { get; init; } = string.Empty;
        public string LatestEventId { get; init; } = string.Empty;
        public string SourceWatermark { get; init; } = string.Empty;
        public DateTime LastSeenAt { get; init; }
    }

    private sealed class LifecycleRow
    {
        public long OpportunityId { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public string StrategyCode { get; init; } = string.Empty;
        public string StrategyVersion { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string Confidence { get; init; } = string.Empty;
        public decimal Score { get; init; }
        public string LatestEventId { get; init; } = string.Empty;
        public string SourceWatermark { get; init; } = string.Empty;
        public DateTime LastSeenAt { get; init; }
        public string Status { get; init; } = string.Empty;
    }
}
