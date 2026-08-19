using System.Text.Json;
using AStockMonitor.Api.Hubs;
using AStockMonitor.Api.Models;
using AStockMonitor.Infrastructure.Market;
using AStockMonitor.Infrastructure.Observability;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace AStockMonitor.Api.Services;

/// <summary>
/// 将策略和对子 Redis Stream 投影为可补拉的网页任务，再通过独立 SignalR Hub 推送。
/// 投影失败时不 ACK，源消息保留在 Pending，不影响原有业务消费者。
/// </summary>
public sealed class NotificationProjectionWorker(
    IConfiguration configuration,
    RedisConnectionProvider redis,
    IMySqlConnectionFactory connectionFactory,
    IHubContext<NotificationHub> hub,
    ILogger<NotificationProjectionWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string StrategyStream => configuration["StrategyEvents:Stream"] ?? "strategy:v1:signal:event";
    private string PairStream => configuration["PairTrendEvents:Stream"] ?? "pair:v3:event";
    private string StrategyGroup => configuration["Notifications:StrategyConsumerGroup"]
                                    ?? "web-notification-strategy-v1";
    private string PairGroup => configuration["Notifications:PairConsumerGroup"]
                                ?? "web-notification-pair-v1";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AStockObservability.ComponentStarted("web-notification-projector");
        try
        {
            await BootstrapAsync(stoppingToken);
            var database = (await redis.GetAsync()).GetDatabase();
            await Task.WhenAll(
                ConsumeAsync(database, StrategyStream, StrategyGroup, "strategy", stoppingToken),
                ConsumeAsync(database, PairStream, PairGroup, "pair", stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ConsumeAsync(
        IDatabase database,
        RedisKey stream,
        RedisValue group,
        string kind,
        CancellationToken cancellationToken)
    {
        await EnsureGroupAsync(database, stream, group);
        var consumer = $"web-{kind}-{Environment.MachineName}-{Environment.ProcessId}";
        await RecoverPendingAsync(database, stream, group, consumer, kind, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var entries = await database.StreamReadGroupAsync(stream, group, consumer, ">", 100);
                if (entries.Length == 0)
                {
                    await Task.Delay(250, cancellationToken);
                    continue;
                }

                foreach (var entry in entries)
                {
                    await ProcessEntryAsync(database, stream, group, entry, kind, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "网页通知{Kind}消费循环失败，2秒后重试。", kind);
                AStockObservability.RecordFailure("web-notification-projector");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private async Task ProcessEntryAsync(
        IDatabase database,
        RedisKey stream,
        RedisValue group,
        StreamEntry entry,
        string kind,
        CancellationToken cancellationToken)
    {
        try
        {
            var fields = entry.Values.ToDictionary(
                static field => field.Name.ToString(),
                static field => field.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);
            var eventId = fields.GetValueOrDefault("event_id");
            var payload = fields.GetValueOrDefault("payload");
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(payload))
            {
                throw new InvalidDataException($"{kind} notification stream entry is incomplete.");
            }

            using var document = JsonDocument.Parse(payload);
            Projection? projection = kind == "strategy"
                ? await LoadStrategyProjectionAsync(document.RootElement, eventId, cancellationToken)
                : await LoadPairProjectionAsync(document.RootElement, eventId, cancellationToken);

            if (projection is not null)
            {
                var change = await UpsertAsync(projection, cancellationToken);
                if (change is not null)
                {
                    await BroadcastAsync(change, cancellationToken);
                }
            }

            await database.StreamAcknowledgeAsync(stream, group, entry.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception,
                "网页通知投影失败，保留Pending。Kind={Kind}, StreamId={StreamId}", kind, entry.Id);
            AStockObservability.RecordFailure("web-notification-projector");
        }
    }

    private async Task<Projection?> LoadStrategyProjectionAsync(
        JsonElement payload,
        string eventId,
        CancellationToken cancellationToken)
    {
        var symbol = Text(payload, "symbol")?.ToUpperInvariant();
        var tradingDateText = Text(payload, "tradingDate");
        if (string.IsNullOrWhiteSpace(symbol) ||
            !DateOnly.TryParse(tradingDateText, out var tradingDate))
        {
            throw new InvalidDataException("Strategy event has no valid symbol/tradingDate.");
        }

        await using var connection = connectionFactory.Create();
        var row = await connection.QuerySingleOrDefaultAsync<StrategyProjectionRow>(new CommandDefinition(
            """
            SELECT o.id Id,o.trading_date TradingDate,o.symbol Symbol,i.name SymbolName,
                   o.level Level,o.status Status,o.primary_strategy_code PrimaryStrategyCode,
                   d.name PrimaryStrategyName,o.highest_score HighestScore,
                   o.strategy_count StrategyCount,o.first_seen_at FirstSeenAt,
                   o.last_seen_at LastSeenAt,o.latest_event_id LatestEventId,
                   e.event_type EventType,e.action Action,e.confidence Confidence,
                   e.hit_price HitPrice,e.stop_reference StopReference,
                   e.target_reference TargetReference,
                   CAST(e.passed_conditions AS CHAR) PassedConditionsJson,
                   e.source_watermark SourceWatermark
            FROM strategy_opportunity o
            LEFT JOIN instrument i ON i.symbol=o.symbol
            LEFT JOIN strategy_definition d ON d.strategy_code=o.primary_strategy_code
            LEFT JOIN strategy_signal_event e ON e.event_id=o.latest_event_id
            WHERE o.trading_date=@TradingDate AND o.symbol=@Symbol;
            """,
            new { TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue), Symbol = symbol },
            cancellationToken: cancellationToken));
        if (row is null)
        {
            logger.LogWarning("策略通知源记录尚未找到。Symbol={Symbol}, Date={Date}", symbol, tradingDate);
            return null;
        }

        var body = JsonSerializer.Serialize(new
        {
            opportunityId = row.Id,
            tradingDate = row.TradingDate,
            level = row.Level,
            status = row.Status,
            primaryStrategyCode = row.PrimaryStrategyCode,
            primaryStrategyName = row.PrimaryStrategyName,
            highestScore = row.HighestScore,
            strategyCount = row.StrategyCount,
            eventType = Text(payload, "eventType") ?? row.EventType,
            action = Text(payload, "action") ?? row.Action,
            confidence = Text(payload, "confidence") ?? row.Confidence,
            hitPrice = Number(payload, "hitPrice") ?? row.HitPrice,
            stopReference = Number(payload, "stopReference") ?? row.StopReference,
            targetReference = Number(payload, "targetReference") ?? row.TargetReference,
            passedConditions = ParseJson(row.PassedConditionsJson, "[]"),
            sourceWatermark = Text(payload, "sourceWatermark") ?? row.SourceWatermark
        }, JsonOptions);
        var name = string.IsNullOrWhiteSpace(row.SymbolName) ? row.Symbol : row.SymbolName;
        var strategyName = string.IsNullOrWhiteSpace(row.PrimaryStrategyName)
            ? row.PrimaryStrategyCode
            : row.PrimaryStrategyName;
        return new Projection(
            $"strategy:{row.TradingDate:yyyyMMdd}:{row.Symbol}",
            "strategy_opportunity",
            row.Id.ToString(),
            row.Symbol,
            row.SymbolName,
            row.Level.ToLowerInvariant(),
            row.Status.ToLowerInvariant(),
            eventId,
            $"{name} · {strategyName}",
            $"{row.StrategyCount}个策略命中，最高分 {row.HighestScore:0.00}",
            body,
            row.FirstSeenAt,
            row.LastSeenAt,
            row.LastSeenAt);
    }

    private async Task<Projection?> LoadPairProjectionAsync(
        JsonElement payload,
        string eventId,
        CancellationToken cancellationToken)
    {
        var eventKey = Text(payload, "eventKey");
        if (string.IsNullOrWhiteSpace(eventKey))
        {
            throw new InvalidDataException("Pair event has no eventKey.");
        }

        await using var connection = connectionFactory.Create();
        var row = await connection.QuerySingleOrDefaultAsync<PairProjectionRow>(new CommandDefinition(
            """
            SELECT id Id,event_key EventKey,symbol Symbol,symbol_name SymbolName,
                   pivot_type PivotType,status Status,first_seen_at FirstSeenAt,
                   last_seen_at LastSeenAt,confirmed_at ConfirmedAt,
                   latest_pair_price LatestPairPrice,latest_pair_code LatestPairCode,
                   latest_pair_kind LatestPairKind,frequencies Frequencies,
                   strongest_frequency StrongestFrequency,confluence_count ConfluenceCount,
                   total_hit_count TotalHitCount,confirmed_hit_count ConfirmedHitCount,
                   pending_hit_count PendingHitCount,invalidated_hit_count InvalidatedHitCount,
                   retracted_hit_count RetractedHitCount,score Score,
                   max_trend_strength MaxTrendStrength,algorithm_version AlgorithmVersion,
                   stage Stage,generation Generation,is_active IsActive,
                   invalidated_at InvalidatedAt,invalidated_price InvalidatedPrice,
                   invalidation_reason InvalidationReason,
                   event_revision EventRevision,last_source_event_id LastSourceEventId
            FROM pair_trend_live_event WHERE event_key=@EventKey;
            """, new { EventKey = eventKey }, cancellationToken: cancellationToken));
        if (row is null)
        {
            logger.LogWarning("对子通知源记录尚未找到。EventKey={EventKey}", eventKey);
            return null;
        }

        var body = JsonSerializer.Serialize(new
        {
            pairEventId = row.Id,
            row.EventKey,
            pivotType = row.PivotType,
            status = row.Status,
            row.LatestPairPrice,
            row.LatestPairCode,
            row.LatestPairKind,
            frequencies = Split(row.Frequencies),
            row.StrongestFrequency,
            row.ConfluenceCount,
            row.TotalHitCount,
            row.ConfirmedHitCount,
            row.PendingHitCount,
            row.InvalidatedHitCount,
            row.RetractedHitCount,
            row.Score,
            row.MaxTrendStrength,
            row.AlgorithmVersion,
            row.Stage,
            row.Generation,
            row.IsActive,
            row.InvalidatedAt,
            row.InvalidatedPrice,
            row.InvalidationReason,
            row.EventRevision,
            row.ConfirmedAt
        }, JsonOptions);
        var name = string.IsNullOrWhiteSpace(row.SymbolName) ? row.Symbol : row.SymbolName;
        var direction = row.PivotType.Equals("TOP", StringComparison.OrdinalIgnoreCase) ? "对子顶部" : "对子底部";
        var pair = row.LatestPairKind.Equals("ROUND_00", StringComparison.OrdinalIgnoreCase)
            ? ".00"
            : $".{row.LatestPairCode:00}";
        var severity = row.Stage.ToUpperInvariant() switch
        {
            "ESTABLISHED" => "level1",
            "FOCUS" => "critical",
            "OBSERVING" => "observe",
            "INVALIDATED" => "resolved",
            _ => "discovered"
        };
        var stageName = row.Stage.ToUpperInvariant() switch
        {
            "ESTABLISHED" => "成立（一级警报）",
            "FOCUS" => "重点",
            "OBSERVING" => "观察",
            "INVALIDATED" => "已失效",
            _ => "发现"
        };
        return new Projection(
            $"pair:{row.EventKey}",
            "pair_trend",
            row.Id.ToString(),
            row.Symbol,
            row.SymbolName,
            severity,
            row.Stage.ToLowerInvariant(),
            eventId,
            $"{name} · {direction}",
            $"{row.LatestPairPrice:0.00} ({pair}) · {stageName} · {row.Frequencies}",
            body,
            row.FirstSeenAt,
            row.LastSeenAt,
            row.LastSeenAt);
    }

    private async Task<NotificationChangeDto?> UpsertAsync(
        Projection projection,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await connection.QuerySingleOrDefaultAsync<ExistingTask>(new CommandDefinition(
            "SELECT id Id,revision Revision,latest_event_id LatestEventId FROM notification_task WHERE task_key=@TaskKey FOR UPDATE;",
            new { projection.TaskKey }, transaction, cancellationToken: cancellationToken));
        if (existing?.LatestEventId == projection.EventId)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var revision = existing is null ? 0 : existing.Revision + 1;
        long taskId;
        var changeType = existing is null ? "created" : "updated";
        if (existing is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO notification_task
                    (task_key,task_type,source_id,symbol,symbol_name,severity,business_status,
                     revision,latest_event_id,title,summary,payload_json,
                     first_seen_at,last_seen_at)
                VALUES
                    (@TaskKey,@TaskType,@SourceId,@Symbol,@SymbolName,@Severity,@BusinessStatus,
                     @Revision,@EventId,@Title,@Summary,CAST(@PayloadJson AS JSON),
                     @FirstSeenAt,@LastSeenAt);
                """, new
                {
                    projection.TaskKey, projection.TaskType, projection.SourceId,
                    projection.Symbol, projection.SymbolName, projection.Severity,
                    projection.BusinessStatus, Revision = revision, projection.EventId,
                    projection.Title, projection.Summary, PayloadJson = projection.Payload,
                    projection.FirstSeenAt, projection.LastSeenAt
                }, transaction, cancellationToken: cancellationToken));
            taskId = await connection.QuerySingleAsync<long>(new CommandDefinition(
                "SELECT LAST_INSERT_ID();", transaction: transaction,
                cancellationToken: cancellationToken));
        }
        else
        {
            taskId = existing.Id;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE notification_task
                SET source_id=@SourceId,symbol=@Symbol,symbol_name=@SymbolName,
                    severity=@Severity,business_status=@BusinessStatus,revision=@Revision,
                    latest_event_id=@EventId,title=@Title,summary=@Summary,
                    payload_json=CAST(@PayloadJson AS JSON),first_seen_at=@FirstSeenAt,
                    last_seen_at=@LastSeenAt
                WHERE id=@Id;
                """, new
                {
                    Id = taskId, projection.SourceId, projection.Symbol, projection.SymbolName,
                    projection.Severity, projection.BusinessStatus, Revision = revision,
                    projection.EventId, projection.Title, projection.Summary,
                    PayloadJson = projection.Payload, projection.FirstSeenAt, projection.LastSeenAt
                }, transaction, cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO notification_task_change
                (task_id,event_id,revision,change_type,occurred_at)
            VALUES (@TaskId,@EventId,@Revision,@ChangeType,@OccurredAt)
            ON DUPLICATE KEY UPDATE event_id=event_id;
            """, new
            {
                TaskId = taskId, projection.EventId, Revision = revision,
                ChangeType = changeType, projection.OccurredAt
            }, transaction, cancellationToken: cancellationToken));
        var changeId = await connection.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT id FROM notification_task_change WHERE event_id=@EventId;",
            new { projection.EventId }, transaction, cancellationToken: cancellationToken));
        var task = await LoadTaskAsync(connection, transaction, taskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new NotificationChangeDto(
            changeId, changeType, projection.EventId, revision, projection.OccurredAt, task);
    }

    private async Task BroadcastAsync(NotificationChangeDto change, CancellationToken cancellationToken)
    {
        await hub.Clients.Group(NotificationHub.AllTasksGroup)
            .SendAsync($"notification.task.{change.ChangeType}", change, cancellationToken);
        AStockObservability.RecordSignalRMessage();
    }

    private async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM notification_task;", cancellationToken: cancellationToken));
        if (count > 0)
        {
            return;
        }

        logger.LogInformation("网页通知表为空，正在从当前策略机会和实时对子事件建立初始投影。");
        var strategies = (await connection.QueryAsync<BootstrapStrategyRow>(new CommandDefinition(
            """
            SELECT trading_date TradingDate,symbol Symbol,latest_event_id EventId
            FROM strategy_opportunity
            WHERE trading_date=(SELECT MAX(trading_date) FROM strategy_opportunity)
              AND status IN ('active','weakened')
            ORDER BY last_seen_at;
            """, cancellationToken: cancellationToken))).AsList();
        foreach (var row in strategies)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                symbol = row.Symbol,
                tradingDate = row.TradingDate.ToString("yyyy-MM-dd")
            }, JsonOptions));
            var projection = await LoadStrategyProjectionAsync(
                document.RootElement, row.EventId, cancellationToken);
            if (projection is not null)
            {
                await UpsertAsync(projection, cancellationToken);
            }
        }

        var pairs = (await connection.QueryAsync<BootstrapPairRow>(new CommandDefinition(
            """
            SELECT event_key EventKey,
                   CONCAT('bootstrap-pair-',id,'-',event_revision) EventId
            FROM pair_trend_live_event
            WHERE last_seen_at>=DATE_SUB(CURRENT_TIMESTAMP(6),INTERVAL 30 DAY)
              AND (stage IN ('OBSERVING','FOCUS','ESTABLISHED')
                   OR (stage='INVALIDATED' AND observed_at IS NOT NULL))
            ORDER BY last_seen_at;
            """, cancellationToken: cancellationToken))).AsList();
        foreach (var row in pairs)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                eventKey = row.EventKey
            }, JsonOptions));
            var projection = await LoadPairProjectionAsync(
                document.RootElement, row.EventId, cancellationToken);
            if (projection is not null)
            {
                await UpsertAsync(projection, cancellationToken);
            }
        }
    }

    private static async Task<NotificationTaskDto> LoadTaskAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        long id,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleAsync<NotificationTaskDto>(new CommandDefinition(
            TaskSelectSql + " WHERE id=@Id;", new { Id = id }, transaction,
            cancellationToken: cancellationToken));

    internal const string TaskSelectSql = """
        SELECT id Id,task_key TaskKey,task_type TaskType,source_id SourceId,
               symbol Symbol,symbol_name SymbolName,severity Severity,
               business_status BusinessStatus,revision Revision,
               latest_event_id LatestEventId,title Title,summary Summary,
               CAST(payload_json AS CHAR) PayloadJson,is_read IsRead,
               is_starred IsStarred,user_status UserStatus,
               first_seen_at FirstSeenAt,last_seen_at LastSeenAt,
               read_at ReadAt,handled_at HandledAt,archived_at ArchivedAt,
               created_at CreatedAt,updated_at UpdatedAt
        FROM notification_task
        """;

    private static async Task EnsureGroupAsync(IDatabase database, RedisKey stream, RedisValue group)
    {
        try
        {
            await database.StreamCreateConsumerGroupAsync(stream, group, StreamPosition.NewMessages, true);
        }
        catch (RedisServerException exception) when (
            exception.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private async Task RecoverPendingAsync(
        IDatabase database,
        RedisKey stream,
        RedisValue group,
        RedisValue consumer,
        string kind,
        CancellationToken cancellationToken)
    {
        RedisValue start = "0-0";
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await database.StreamAutoClaimAsync(stream, group, consumer, 60_000, start, 100);
            if (result.ClaimedEntries.Length == 0)
            {
                return;
            }
            foreach (var entry in result.ClaimedEntries)
            {
                await ProcessEntryAsync(database, stream, group, entry, kind, cancellationToken);
            }
            if (result.ClaimedEntries.Length < 100)
            {
                return;
            }
            start = result.NextStartId;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static decimal? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number)
            ? number
            : null;

    private static object ParseJson(string? json, string fallback)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(json ?? fallback); }
        catch (JsonException) { return JsonSerializer.Deserialize<JsonElement>(fallback); }
    }

    private static string[] Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record Projection(
        string TaskKey,
        string TaskType,
        string SourceId,
        string Symbol,
        string? SymbolName,
        string Severity,
        string BusinessStatus,
        string EventId,
        string Title,
        string Summary,
        string Payload,
        DateTime FirstSeenAt,
        DateTime LastSeenAt,
        DateTime OccurredAt);

    private sealed class ExistingTask { public long Id { get; init; } public int Revision { get; init; } public string LatestEventId { get; init; } = string.Empty; }
    private sealed class BootstrapStrategyRow { public DateTime TradingDate { get; init; } public string Symbol { get; init; } = string.Empty; public string EventId { get; init; } = string.Empty; }
    private sealed class BootstrapPairRow { public string EventKey { get; init; } = string.Empty; public string EventId { get; init; } = string.Empty; }
    private sealed class StrategyProjectionRow
    {
        public long Id { get; init; }
        public DateTime TradingDate { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public string? SymbolName { get; init; }
        public string Level { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string PrimaryStrategyCode { get; init; } = string.Empty;
        public string? PrimaryStrategyName { get; init; }
        public decimal HighestScore { get; init; }
        public int StrategyCount { get; init; }
        public DateTime FirstSeenAt { get; init; }
        public DateTime LastSeenAt { get; init; }
        public string LatestEventId { get; init; } = string.Empty;
        public string? EventType { get; init; }
        public string? Action { get; init; }
        public string? Confidence { get; init; }
        public decimal HitPrice { get; init; }
        public decimal? StopReference { get; init; }
        public decimal? TargetReference { get; init; }
        public string? PassedConditionsJson { get; init; }
        public string? SourceWatermark { get; init; }
    }

    private sealed class PairProjectionRow
    {
        public long Id { get; init; }
        public string EventKey { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string? SymbolName { get; init; }
        public string PivotType { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public DateTime FirstSeenAt { get; init; }
        public DateTime LastSeenAt { get; init; }
        public DateTime? ConfirmedAt { get; init; }
        public decimal LatestPairPrice { get; init; }
        public int LatestPairCode { get; init; }
        public string LatestPairKind { get; init; } = string.Empty;
        public string Frequencies { get; init; } = string.Empty;
        public string StrongestFrequency { get; init; } = string.Empty;
        public int ConfluenceCount { get; init; }
        public int TotalHitCount { get; init; }
        public int ConfirmedHitCount { get; init; }
        public int PendingHitCount { get; init; }
        public int InvalidatedHitCount { get; init; }
        public int RetractedHitCount { get; init; }
        public decimal Score { get; init; }
        public decimal MaxTrendStrength { get; init; }
        public string AlgorithmVersion { get; init; } = string.Empty;
        public string Stage { get; init; } = string.Empty;
        public int Generation { get; init; }
        public bool IsActive { get; init; }
        public DateTime? InvalidatedAt { get; init; }
        public decimal? InvalidatedPrice { get; init; }
        public string? InvalidationReason { get; init; }
        public int EventRevision { get; init; }
        public string LastSourceEventId { get; init; } = string.Empty;
    }
}
