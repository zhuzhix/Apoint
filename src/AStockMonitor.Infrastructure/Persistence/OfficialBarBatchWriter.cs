using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AStockMonitor.Application.Market;
using AStockMonitor.Contracts.Market;
using AStockMonitor.Domain.Market;
using AStockMonitor.Infrastructure.Market;
using Dapper;
using MySqlConnector;

namespace AStockMonitor.Infrastructure.Persistence;

/// <summary>
/// The only writer for a Gateway official-bar batch. One transaction commits
/// the receipt, staging rows, canonical bars, checkpoints, outbox, recovery
/// progress and the replay tasks. A deadlock retries the whole transaction.
/// </summary>
public sealed class OfficialBarBatchWriter(IMySqlConnectionFactory connectionFactory)
    : IOfficialBarBatchWriter
{
    public async Task<OfficialBarBatchWriteResult> WriteAsync(
        OfficialBarBatchInput input, CancellationToken cancellationToken)
    {
        ValidateInput(input);
        var bars = input.Bars.Select(Normalize).OrderBy(static bar => bar.Frequency)
            .ThenBy(static bar => bar.TradingDate).ThenBy(static bar => bar.Symbol, StringComparer.Ordinal)
            .ThenBy(static bar => bar.Eob).ToArray();
        foreach (var bar in bars) ValidateBar(bar);
        var payloadHash = Sha256(JsonSerializer.Serialize(bars));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return await WriteTransactionAsync(input, bars, payloadHash, cancellationToken);
            }
            catch (MySqlException exception) when (exception.Number is 1205 or 1213 && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1) + Random.Shared.Next(30)), cancellationToken);
            }
            catch (Exception exception)
            {
                await MarkFailedAsync(input, exception.Message, cancellationToken);
                throw;
            }
        }
        await MarkFailedAsync(input, "MySQL deadlock retry budget exhausted.", cancellationToken);
        throw new InvalidOperationException("Official-bar batch deadlock retry budget exhausted.");
    }

    private async Task<OfficialBarBatchWriteResult> WriteTransactionAsync(
        OfficialBarBatchInput input, IReadOnlyCollection<CanonicalBarInput> bars,
        string payloadHash, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var commandId = input.CommandId.ToString();
        var batchId = input.BatchId.ToString();
        var commandStatus = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            """
            SELECT status FROM collector_command
            WHERE command_id=@CommandId AND gateway_id=@GatewayId AND command_type='history_collection'
            FOR UPDATE;
            """, new { CommandId = commandId, input.GatewayId }, transaction,
            cancellationToken: cancellationToken));
        if (commandStatus is not ("dispatched" or "acknowledged"))
            throw new InvalidOperationException("Official-bar batch does not belong to an active history command for this gateway.");

        var existingBatch = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            """
            SELECT status FROM collector_result_batch
            WHERE command_id=@CommandId AND batch_id=@BatchId FOR UPDATE;
            """, new { CommandId = commandId, BatchId = batchId }, transaction,
            cancellationToken: cancellationToken));
        if (existingBatch is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            if (existingBatch.Equals("applied", StringComparison.Ordinal))
                return new OfficialBarBatchWriteResult(true, 0, bars.Count, 0, "duplicate batch");
            throw new InvalidOperationException($"Official-bar batch {input.BatchId} is already {existingBatch}; operator action is required.");
        }

        var recoveryRunId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            """
            SELECT recovery_run_id FROM market_recovery_item
            WHERE id=@RecoveryItemId AND status='dispatched' FOR UPDATE;
            """, new { input.RecoveryItemId }, transaction, cancellationToken: cancellationToken));
        if (recoveryRunId is null)
            throw new InvalidOperationException("Official-bar batch recovery item is not dispatched.");

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO collector_result_batch
                (command_id,batch_id,gateway_id,worker_id,result_type,status,item_count,payload_hash)
            VALUES (@CommandId,@BatchId,@GatewayId,@WorkerId,'official_bar','applying',@ItemCount,@PayloadHash);
            UPDATE market_recovery_item SET status='applying',last_error=NULL WHERE id=@RecoveryItemId;
            """, new { CommandId = commandId, BatchId = batchId, input.GatewayId, input.WorkerId,
                ItemCount = bars.Count, PayloadHash = payloadHash, input.RecoveryItemId }, transaction,
            cancellationToken: cancellationToken));

        var changedBars = new List<CanonicalBarInput>();
        var itemIndex = 0;
        foreach (var bar in bars)
        {
            var persisted = bar with { RecoveryRunId = recoveryRunId };
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO official_bar_staging
                    (command_id,batch_id,item_index,recovery_item_id,payload,status)
                VALUES (@CommandId,@BatchId,@ItemIndex,@RecoveryItemId,CAST(@Payload AS JSON),'applying');
                """, new { CommandId = commandId, BatchId = batchId, ItemIndex = itemIndex++,
                    input.RecoveryItemId, Payload = JsonSerializer.Serialize(persisted) }, transaction,
                cancellationToken: cancellationToken));
            if (await ApplyBarAsync(connection, transaction, persisted, cancellationToken))
                changedBars.Add(persisted);
        }

        await MarkAppliedAsync(connection, transaction, input, changedBars, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OfficialBarBatchWriteResult(true, bars.Count, 0, 0);
    }

    private static async Task<bool> ApplyBarAsync(MySqlConnection connection, MySqlTransaction transaction,
        CanonicalBarInput input, CancellationToken cancellationToken)
    {
        var mapping = Mapping.For(input.Frequency);
        var existing = await connection.QuerySingleOrDefaultAsync<ExistingBar>(new CommandDefinition(
            $"""
            SELECT row_hash RowHash,revision Revision,source_priority SourcePriority FROM {mapping.Table}
            WHERE symbol=@Symbol AND trading_date=@TradingDate AND eob=@Eob {mapping.FrequencyPredicate}
            FOR UPDATE;
            """, ToParameters(input, 0), transaction, cancellationToken: cancellationToken));
        var contentChanged = existing is null || !existing.RowHash.Equals(input.RowHash, StringComparison.OrdinalIgnoreCase);
        var changed = contentChanged && (existing is null || existing.SourcePriority <= 300);
        var revision = existing is null ? 0 : changed ? existing.Revision + 1 : existing.Revision;
        if (existing is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(mapping.InsertSql, ToParameters(input, revision), transaction,
                cancellationToken: cancellationToken));
        }
        else if (changed)
        {
            await connection.ExecuteAsync(new CommandDefinition($"""
                UPDATE {mapping.Table}
                SET bob=@Bob,open_price=@OpenPrice,high_price=@HighPrice,low_price=@LowPrice,
                    close_price=@ClosePrice,pre_close=@PreClose,volume=@Volume,amount=@Amount,
                    source=@Source,source_priority=300,source_updated_at=@SourceUpdatedAt,
                    official_confirmed=TRUE,revision=@Revision,quality_status='passed',recovery_run_id=@RecoveryRunId,
                    row_hash=@RowHash,updated_at=CURRENT_TIMESTAMP(6)
                WHERE symbol=@Symbol AND trading_date=@TradingDate AND eob=@Eob {mapping.FrequencyPredicate};
                """, ToParameters(input, revision), transaction, cancellationToken: cancellationToken));
        }
        await UpsertCheckpointAsync(connection, transaction, input, cancellationToken);
        if (changed) await InsertLifecycleEventAsync(connection, transaction, input, existing, revision, cancellationToken);
        return changed;
    }

    private static Task UpsertCheckpointAsync(MySqlConnection connection, MySqlTransaction transaction,
        CanonicalBarInput input, CancellationToken cancellationToken) => connection.ExecuteAsync(new CommandDefinition("""
        INSERT INTO bar_sync_checkpoint
            (symbol,frequency,last_seen_eob,last_closed_eob,last_persisted_eob,last_source_updated_at,status)
        VALUES (@Symbol,@Frequency,@Eob,@Eob,@Eob,@SourceUpdatedAt,'healthy')
        ON DUPLICATE KEY UPDATE
            last_seen_eob=GREATEST(COALESCE(last_seen_eob,VALUES(last_seen_eob)),VALUES(last_seen_eob)),
            last_closed_eob=GREATEST(COALESCE(last_closed_eob,VALUES(last_closed_eob)),VALUES(last_closed_eob)),
            last_persisted_eob=GREATEST(COALESCE(last_persisted_eob,VALUES(last_persisted_eob)),VALUES(last_persisted_eob)),
            last_source_updated_at=VALUES(last_source_updated_at),status='healthy',consecutive_failures=0;
        """, ToParameters(input, 0), transaction, cancellationToken: cancellationToken));

    private static async Task InsertLifecycleEventAsync(MySqlConnection connection, MySqlTransaction transaction,
        CanonicalBarInput input, ExistingBar? existing, int revision, CancellationToken cancellationToken)
    {
        var eventType = existing is null ? "BarClosed" : "BarRevised";
        var eventId = "sha256:" + Sha256($"official|{input.Symbol}|{input.Frequency}|{input.Eob:O}|{revision}|{input.RowHash}");
        var payload = BarLifecycleEventV2Json.Serialize(new BarLifecycleEventV2(
            BarLifecycleEventV2.CurrentSchemaVersion,eventId,eventType,input.Symbol,input.Frequency,input.TradingDate,
            input.Bob,input.Eob,revision,input.RowHash,input.Source,input.SourceUpdatedAt,true,input.CollectionMode,
            input.RecoveryRunId,DateTimeOffset.UtcNow,new BarLifecyclePayloadV2(input.OpenPrice,input.HighPrice,
            input.LowPrice,input.ClosePrice,input.PreClose,input.Volume,input.Amount)));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO bar_event_outbox
                (event_id,event_type,symbol,frequency,trading_date,bob,eob,revision,row_hash,payload,status)
            VALUES (@EventId,@EventType,@Symbol,@Frequency,@TradingDate,@Bob,@Eob,@Revision,@RowHash,CAST(@Payload AS JSON),'pending')
            ON DUPLICATE KEY UPDATE event_id=event_id;
            """, new { EventId = eventId,EventType = eventType,input.Symbol,input.Frequency,
                TradingDate = input.TradingDate.ToDateTime(TimeOnly.MinValue),Bob = ToChinaLocal(input.Bob),
                Eob = ToChinaLocal(input.Eob),Revision = revision,input.RowHash,Payload = payload }, transaction,
            cancellationToken: cancellationToken));
        if (existing is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO bar_reconcile_log
                    (reconcile_key,symbol,frequency,trading_date,eob,result_type,old_row_hash,new_row_hash,reason,recovery_run_id)
                VALUES (@Key,@Symbol,@Frequency,@TradingDate,@Eob,'source_mismatch',@OldHash,@NewHash,@Reason,@RecoveryRunId)
                ON DUPLICATE KEY UPDATE checked_at=CURRENT_TIMESTAMP(6);
                """, new { Key = Sha256($"reconcile|{input.Symbol}|{input.Frequency}|{input.Eob:O}|{input.RowHash}"),
                input.Symbol,input.Frequency,TradingDate = input.TradingDate.ToDateTime(TimeOnly.MinValue),
                Eob = ToChinaLocal(input.Eob),OldHash = existing.RowHash,NewHash = input.RowHash,
                Reason = input.CollectionMode,input.RecoveryRunId }, transaction, cancellationToken: cancellationToken));
        }
    }

    private static async Task MarkAppliedAsync(MySqlConnection connection, MySqlTransaction transaction,
        OfficialBarBatchInput input, IReadOnlyCollection<CanonicalBarInput> changedBars, CancellationToken cancellationToken)
    {
        var commandId = input.CommandId.ToString();
        var batchId = input.BatchId.ToString();
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE collector_result_batch SET status='applied',applied_at=UTC_TIMESTAMP(6),last_error=NULL
            WHERE command_id=@CommandId AND batch_id=@BatchId;
            UPDATE official_bar_staging SET status='applied',applied_at=UTC_TIMESTAMP(6)
            WHERE command_id=@CommandId AND batch_id=@BatchId;
            UPDATE market_recovery_item SET status='completed',completed_at=UTC_TIMESTAMP(6),lease_owner=NULL,lease_expires_at=NULL
            WHERE id=@RecoveryItemId AND status='applying';
            """, new { CommandId = commandId,BatchId = batchId,input.RecoveryItemId }, transaction,
            cancellationToken: cancellationToken));
        foreach (var group in changedBars.GroupBy(static bar => new { bar.Symbol,bar.TradingDate }))
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO strategy_replay_task (task_id,symbol,date_from,date_to,source_command_id,status)
                VALUES (@TaskId,@Symbol,DATE_SUB(@Date,INTERVAL 45 DAY),DATE_ADD(@Date,INTERVAL 5 DAY),@CommandId,'pending')
                ON DUPLICATE KEY UPDATE task_id=task_id;
                """, new { TaskId = Guid.NewGuid().ToString(),group.Key.Symbol,
                Date = group.Key.TradingDate.ToDateTime(TimeOnly.MinValue),CommandId = commandId }, transaction,
                cancellationToken: cancellationToken));
        }
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE collector_command c SET status='completed',completed_at=UTC_TIMESTAMP(6),last_error=NULL
            WHERE c.command_id=@CommandId AND NOT EXISTS (
                SELECT 1 FROM JSON_TABLE(c.payload,'$.recoveryItemIds[*]' COLUMNS (item_id BIGINT PATH '$')) command_items
                INNER JOIN market_recovery_item item ON item.id=command_items.item_id
                WHERE item.status NOT IN ('completed','verified_no_bar')
            );
            UPDATE market_recovery_run run_row SET status='completed',finished_at=UTC_TIMESTAMP(6),error_message=NULL
            WHERE run_row.id=(SELECT recovery_run_id FROM market_recovery_item WHERE id=@RecoveryItemId)
              AND NOT EXISTS (SELECT 1 FROM market_recovery_item remaining WHERE remaining.recovery_run_id=run_row.id
                              AND remaining.status NOT IN ('completed','verified_no_bar'));
            """, new { CommandId = commandId,input.RecoveryItemId }, transaction, cancellationToken: cancellationToken));
    }

    private async Task MarkFailedAsync(OfficialBarBatchInput input, string error, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE collector_result_batch SET status='failed',last_error=@Error WHERE command_id=@CommandId AND batch_id=@BatchId;
            UPDATE official_bar_staging SET status='failed' WHERE command_id=@CommandId AND batch_id=@BatchId;
            UPDATE market_recovery_item SET status='failed',last_error=@Error
            WHERE id=@RecoveryItemId AND status IN ('dispatched','received','applying');
            """, new { CommandId = input.CommandId.ToString(),BatchId = input.BatchId.ToString(),input.RecoveryItemId,
            Error = error.Length <= 1024 ? error : error[..1024] }, cancellationToken: cancellationToken));
    }

    private static CanonicalBarInput Normalize(CanonicalBarInput input)
    {
        var value = input with { Symbol = input.Symbol.Trim().ToUpperInvariant(),Frequency = input.Frequency.Trim().ToLowerInvariant(),
            Source = string.IsNullOrWhiteSpace(input.Source) ? "dongcai-gm" : input.Source.Trim() };
        return string.IsNullOrWhiteSpace(value.RowHash) ? value with { RowHash = Sha256(string.Join('|',value.Symbol,value.Frequency,
            value.TradingDate,value.Bob,value.Eob,value.OpenPrice,value.HighPrice,value.LowPrice,value.ClosePrice,value.PreClose,
            value.Volume,value.Amount,value.Source)) } : value;
    }

    private static void ValidateInput(OfficialBarBatchInput input)
    {
        if (input.CommandId == Guid.Empty || input.BatchId == Guid.Empty || input.RecoveryItemId <= 0 ||
            string.IsNullOrWhiteSpace(input.GatewayId) || string.IsNullOrWhiteSpace(input.WorkerId) || input.Bars.Count == 0)
            throw new ArgumentException("Official-bar batch identity, recovery item and bars are required.");
        if (input.Bars.Any(static bar => !bar.IsClosed)) throw new ArgumentException("Official-bar batches only accept closed bars.");
    }

    private static void ValidateBar(CanonicalBarInput input)
    {
        if (!MarketBarFrequencies.IsSupported(input.Frequency) || string.IsNullOrWhiteSpace(input.Symbol) ||
            input.OpenPrice <= 0 || input.HighPrice <= 0 || input.LowPrice <= 0 || input.ClosePrice <= 0 ||
            input.HighPrice < Math.Max(input.OpenPrice,input.ClosePrice) || input.LowPrice > Math.Min(input.OpenPrice,input.ClosePrice) ||
            input.HighPrice < input.LowPrice || input.Volume < 0 || input.Amount < 0)
            throw new ArgumentException("Official Bar failed validation.");
    }

    private static object ToParameters(CanonicalBarInput input, int revision) => new
    {
        input.Symbol,input.Frequency,TradingDate = input.TradingDate.ToDateTime(TimeOnly.MinValue),Bob = ToChinaLocal(input.Bob),
        Eob = ToChinaLocal(input.Eob),input.OpenPrice,input.HighPrice,input.LowPrice,input.ClosePrice,input.PreClose,
        input.Volume,input.Amount,input.Source,SourceUpdatedAt = ToChinaLocal(input.SourceUpdatedAt),Revision = revision,
        input.RecoveryRunId,input.RowHash
    };
    private static DateTime ToChinaLocal(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value,ChinaMarketSession.TimeZone).DateTime;
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class ExistingBar { public string RowHash { get; init; } = string.Empty; public int Revision { get; init; } public int SourcePriority { get; init; } }
    private sealed record Mapping(string Table,string FrequencyPredicate,string InsertSql)
    {
        public static Mapping For(string frequency) => frequency switch
        {
            "5m" => Simple("kline_bar_5m"),"1d" => Simple("kline_bar_daily"),"30m" or "60m" => Aggregate(),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency))
        };
        private static Mapping Simple(string table) => new(table,string.Empty,$"""
            INSERT INTO {table} (symbol,trading_date,bob,eob,open_price,high_price,low_price,close_price,pre_close,volume,amount,source,adjust_mode,source_priority,source_updated_at,official_confirmed,revision,quality_status,recovery_run_id,row_hash)
            VALUES (@Symbol,@TradingDate,@Bob,@Eob,@OpenPrice,@HighPrice,@LowPrice,@ClosePrice,@PreClose,@Volume,@Amount,@Source,'none',300,@SourceUpdatedAt,TRUE,@Revision,'passed',@RecoveryRunId,@RowHash);
            """);
        private static Mapping Aggregate() => new("kline_bar_agg","AND frequency=@Frequency","""
            INSERT INTO kline_bar_agg (symbol,frequency,trading_date,bob,eob,open_price,high_price,low_price,close_price,pre_close,volume,amount,component_count,expected_component_count,source,source_priority,source_updated_at,official_confirmed,revision,quality_status,algorithm_version,recovery_run_id,row_hash)
            VALUES (@Symbol,@Frequency,@TradingDate,@Bob,@Eob,@OpenPrice,@HighPrice,@LowPrice,@ClosePrice,@PreClose,@Volume,@Amount,1,1,@Source,300,@SourceUpdatedAt,TRUE,@Revision,'passed','official-gm-v2',@RecoveryRunId,@RowHash);
            """);
    }
}
