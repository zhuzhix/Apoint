using AStockMonitor.Application.Collection;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Infrastructure.Collection;

public sealed class MySqlCollectorCommandRepository(
    IMySqlConnectionFactory connectionFactory) : ICollectorCommandRepository
{
    public async Task<int> ReplaceTickAssignmentsAsync(
        string gatewayId,
        IReadOnlyCollection<TickSubscriptionAssignment> assignments,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var created = 0;
        foreach (var assignment in assignments)
        {
            var workerId = assignment.WorkerId.Trim();
            var symbols = assignment.Symbols.Select(static symbol => symbol.Trim().ToUpperInvariant())
                .Where(static symbol => !string.IsNullOrWhiteSpace(symbol)).Distinct(StringComparer.Ordinal).ToArray();
            if (string.IsNullOrWhiteSpace(workerId) || string.IsNullOrWhiteSpace(assignment.AssignmentVersion))
                throw new ArgumentException("Tick assignment requires a worker and version.");
            var current = await connection.QuerySingleOrDefaultAsync<TickAssignmentRow>(new CommandDefinition(
                """
                SELECT assignment_version AssignmentVersion, status Status
                FROM collector_tick_assignment
                WHERE gateway_id=@GatewayId AND worker_id=@WorkerId FOR UPDATE;
                """, new { GatewayId = gatewayId.Trim(), WorkerId = workerId }, transaction,
                cancellationToken: cancellationToken));
            if (current is not null && string.Equals(current.AssignmentVersion, assignment.AssignmentVersion, StringComparison.Ordinal) &&
                current.Status is "pending" or "dispatched" or "applied") continue;
            var commandId = Guid.NewGuid();
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                commandId, workerId, assignmentVersion = assignment.AssignmentVersion, symbols
            });
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO collector_command
                    (command_id,gateway_id,worker_id,command_type,payload,status,expires_at)
                VALUES (@CommandId,@GatewayId,@WorkerId,'tick_subscription',CAST(@Payload AS JSON),'pending',DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 1 DAY));
                INSERT INTO collector_tick_assignment
                    (gateway_id,worker_id,assignment_version,symbols,command_id,status,applied_at,last_error)
                VALUES (@GatewayId,@WorkerId,@Version,CAST(@Symbols AS JSON),@CommandId,'pending',NULL,NULL)
                ON DUPLICATE KEY UPDATE assignment_version=VALUES(assignment_version),symbols=VALUES(symbols),
                    command_id=VALUES(command_id),status='pending',applied_at=NULL,last_error=NULL;
                """, new { CommandId = commandId.ToString(), GatewayId = gatewayId.Trim(), WorkerId = workerId,
                    Version = assignment.AssignmentVersion, Payload = payload,
                    Symbols = System.Text.Json.JsonSerializer.Serialize(symbols) }, transaction,
                cancellationToken: cancellationToken));
            created++;
        }
        await transaction.CommitAsync(cancellationToken);
        return created;
    }
    public async Task<Guid?> CreateSnapshotCommandAsync(
        string gatewayId, IReadOnlyCollection<string> symbols, string universeVersion,
        int staleSeconds, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0) return null;
        var commandId = Guid.NewGuid();
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            commandId,
            universeVersion,
            symbols = symbols.Order(StringComparer.Ordinal).ToArray(),
            fields = "symbol,price,cum_volume,cum_amount,last_volume,last_amount,quotes,created_at",
            staleSeconds = Math.Clamp(staleSeconds, 1, 120)
        });
        await using var connection = connectionFactory.Create();
        var created = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO collector_command
                (command_id,gateway_id,command_type,payload,status,expires_at)
            SELECT @CommandId,@GatewayId,'snapshot_collection',CAST(@Payload AS JSON),'pending',@ExpiresAt
            WHERE NOT EXISTS (
                SELECT 1 FROM collector_command
                WHERE gateway_id=@GatewayId AND command_type='snapshot_collection'
                  AND status IN ('pending','dispatched','acknowledged') AND expires_at > UTC_TIMESTAMP(6)
            );
            """,
            new { CommandId = commandId.ToString(), GatewayId = gatewayId.Trim(), Payload = payload,
                ExpiresAt = DateTime.UtcNow.Add(timeout) }, cancellationToken: cancellationToken));
        return created == 1 ? commandId : null;
    }
    public async Task<int> CreateHistoryCommandsAsync(
        long recoveryRunId,
        string gatewayId,
        int symbolsPerCommand,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gatewayId))
            throw new ArgumentException("Gateway id is required.", nameof(gatewayId));
        if (symbolsPerCommand is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(symbolsPerCommand));

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE collector_command c
            JOIN JSON_TABLE(c.payload, '$.recoveryItemIds[*]'
                COLUMNS (item_id BIGINT PATH '$')) command_items ON TRUE
            JOIN market_recovery_item i ON i.id=command_items.item_id
            SET c.status='failed',c.completed_at=UTC_TIMESTAMP(6),
                c.last_error='CollectorGateway command expired before completion',
                i.status=IF(i.retry_count+1>=5,'failed','planned'),
                i.retry_count=i.retry_count+1,
                i.next_retry_at=IF(i.retry_count+1>=5,NULL,
                    TIMESTAMPADD(SECOND,LEAST(300,POW(2,LEAST(i.retry_count,8))),UTC_TIMESTAMP(6))),
                i.lease_owner=NULL,i.lease_expires_at=NULL,
                i.last_error='CollectorGateway command expired before completion'
            WHERE c.gateway_id=@GatewayId AND c.command_type='history_collection'
              AND c.status IN ('pending','dispatched','acknowledged')
              AND c.expires_at<=UTC_TIMESTAMP(6) AND i.recovery_run_id=@RecoveryRunId
              AND i.status='dispatched';
            """, new { GatewayId = gatewayId.Trim(), RecoveryRunId = recoveryRunId }, transaction,
            cancellationToken: cancellationToken));
        var items = (await connection.QueryAsync<HistoryItem>(new CommandDefinition(
            """
            SELECT id Id, symbol Symbol, frequency Frequency, gap_start GapStart, gap_end GapEnd
            FROM market_recovery_item
            WHERE recovery_run_id=@RecoveryRunId AND status='planned'
              AND (next_retry_at IS NULL OR next_retry_at<=UTC_TIMESTAMP(6))
            ORDER BY frequency, gap_start, gap_end, symbol
            FOR UPDATE;
            """,
            new { RecoveryRunId = recoveryRunId }, transaction,
            cancellationToken: cancellationToken))).ToArray();

        var now = DateTimeOffset.UtcNow;
        var count = 0;
        foreach (var group in items.GroupBy(item => new { item.Frequency, item.GapStart, item.GapEnd }))
        {
            foreach (var partition in group.Chunk(symbolsPerCommand))
            {
                var commandId = Guid.NewGuid();
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    commandId,
                    recoveryRunId,
                    recoveryItemIds = partition.Select(item => item.Id).ToArray(),
                    symbols = partition.Select(item => item.Symbol).ToArray(),
                    frequency = group.Key.Frequency,
                    start = DateTime.SpecifyKind(group.Key.GapStart, DateTimeKind.Local),
                    end = DateTime.SpecifyKind(group.Key.GapEnd, DateTimeKind.Local)
                });
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO collector_command
                        (command_id,gateway_id,command_type,payload,status,expires_at)
                    VALUES
                        (@CommandId,@GatewayId,'history_collection',CAST(@Payload AS JSON),'pending',@ExpiresAt);
                    """,
                    new
                    {
                        CommandId = commandId.ToString(),
                        GatewayId = gatewayId.Trim(),
                        Payload = payload,
                        ExpiresAt = now.Add(timeout).UtcDateTime
                    }, transaction, cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE market_recovery_item
                    SET status='dispatched', lease_owner=NULL, lease_expires_at=NULL,
                        next_retry_at=NULL,last_error=NULL
                    WHERE id IN @ItemIds AND status='planned';
                    """,
                    new { ItemIds = partition.Select(item => item.Id).ToArray() }, transaction,
                    cancellationToken: cancellationToken));
                count++;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<IReadOnlyCollection<CollectorCommand>> ClaimPendingAsync(
        string gatewayId, int maxCount, CancellationToken cancellationToken)
    {
        if (maxCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxCount));
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<CommandRow>(new CommandDefinition(
            """
            SELECT CAST(command_id AS CHAR(36)) CommandId, gateway_id GatewayId, worker_id WorkerId,
                   command_type CommandType, CAST(payload AS CHAR) PayloadJson, status Status,
                   attempt_count AttemptCount, expires_at ExpiresAt, created_at CreatedAt,
                   last_error LastError
            FROM collector_command
            WHERE gateway_id=@GatewayId AND status='pending' AND expires_at > UTC_TIMESTAMP(6)
            ORDER BY created_at
            LIMIT @MaxCount
            FOR UPDATE SKIP LOCKED;
            """,
            new { GatewayId = gatewayId.Trim(), MaxCount = maxCount }, transaction,
            cancellationToken: cancellationToken))).ToArray();
        if (rows.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE collector_command
                SET status='dispatched', attempt_count=attempt_count+1, dispatched_at=UTC_TIMESTAMP(6)
                WHERE command_id IN @CommandIds;
                """,
                new { CommandIds = rows.Select(row => row.CommandId).ToArray() }, transaction,
                cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return rows.Select(row => new CollectorCommand(
            Guid.Parse(row.CommandId), row.GatewayId, row.WorkerId, row.CommandType,
            row.PayloadJson, "dispatched", row.AttemptCount + 1,
            new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)), row.LastError)).ToArray();
    }

    public Task MarkAcknowledgedAsync(Guid commandId, string gatewayId, CancellationToken cancellationToken) =>
        UpdateCommandAsync(commandId, gatewayId, "acknowledged", null, false, cancellationToken);

    public Task MarkCompletedAsync(Guid commandId, string gatewayId, CancellationToken cancellationToken) =>
        UpdateCommandAsync(commandId, gatewayId, "completed", null, true, cancellationToken);

    public Task MarkFailedAsync(Guid commandId, string gatewayId, string error, CancellationToken cancellationToken) =>
        UpdateCommandAsync(commandId, gatewayId, "failed", error, true, cancellationToken);

    public async Task RecordGatewayHeartbeatAsync(
        string gatewayId, string displayName, int protocolVersion, string status,
        string? error, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO collector_gateway
                (gateway_id,display_name,status,protocol_version,last_seen_at,last_error)
            VALUES (@GatewayId,@DisplayName,@Status,@ProtocolVersion,UTC_TIMESTAMP(6),@Error)
            ON DUPLICATE KEY UPDATE display_name=VALUES(display_name), status=VALUES(status),
                protocol_version=VALUES(protocol_version), last_seen_at=VALUES(last_seen_at),
                last_error=VALUES(last_error);
            """,
            new { GatewayId = gatewayId.Trim(), DisplayName = displayName.Trim(), Status = status,
                ProtocolVersion = protocolVersion, Error = string.IsNullOrWhiteSpace(error) ? null : error.Trim() },
            cancellationToken: cancellationToken));
    }

    private async Task UpdateCommandAsync(Guid commandId, string gatewayId, string status,
        string? error, bool terminal, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE collector_command
            SET status=@Status, last_error=@Error,
                acknowledged_at=IF(@Status='acknowledged',UTC_TIMESTAMP(6),acknowledged_at),
                completed_at=IF(@Terminal,UTC_TIMESTAMP(6),completed_at)
            WHERE command_id=@CommandId AND gateway_id=@GatewayId
              AND (status IN ('dispatched','acknowledged')
                   OR (status='completed' AND command_type='tick_subscription'));
            """,
            new { CommandId = commandId.ToString(), GatewayId = gatewayId.Trim(), Status = status,
                Error = string.IsNullOrWhiteSpace(error) ? null : error.Trim(), Terminal = terminal },
            transaction, cancellationToken: cancellationToken));
        if (status == "failed")
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE market_recovery_item i
                JOIN collector_command c ON c.command_id=@CommandId AND c.gateway_id=@GatewayId
                JOIN JSON_TABLE(c.payload, '$.recoveryItemIds[*]'
                    COLUMNS (item_id BIGINT PATH '$')) items ON items.item_id=i.id
                SET i.status=IF(i.retry_count+1>=5,'failed','planned'),
                    i.retry_count=i.retry_count+1,
                    i.next_retry_at=IF(i.retry_count+1>=5,NULL,
                        TIMESTAMPADD(SECOND,LEAST(300,POW(2,LEAST(i.retry_count,8))),UTC_TIMESTAMP(6))),
                    i.lease_owner=NULL,i.lease_expires_at=NULL,i.last_error=@Error
                WHERE i.status='dispatched';
                """, new { CommandId = commandId.ToString(), GatewayId = gatewayId.Trim(),
                    Error = string.IsNullOrWhiteSpace(error) ? null : error.Trim() }, transaction,
                cancellationToken: cancellationToken));
        }
        if (status is "completed" or "failed")
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE collector_tick_assignment
                SET status=IF(@Status='completed','applied','failed'),
                    applied_at=IF(@Status='completed',UTC_TIMESTAMP(6),applied_at),last_error=@Error
                WHERE command_id=@CommandId AND gateway_id=@GatewayId;
                """, new { CommandId = commandId.ToString(), GatewayId = gatewayId.Trim(), Status = status,
                    Error = string.IsNullOrWhiteSpace(error) ? null : error.Trim() }, transaction,
                cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private sealed class HistoryItem
    {
        public long Id { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public string Frequency { get; init; } = string.Empty;
        public DateTime GapStart { get; init; }
        public DateTime GapEnd { get; init; }
    }

    private sealed class CommandRow
    {
        public string CommandId { get; init; } = string.Empty;
        public string GatewayId { get; init; } = string.Empty;
        public string? WorkerId { get; init; }
        public string CommandType { get; init; } = string.Empty;
        public string PayloadJson { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public int AttemptCount { get; init; }
        public DateTime ExpiresAt { get; init; }
        public DateTime CreatedAt { get; init; }
        public string? LastError { get; init; }
    }

    private sealed class TickAssignmentRow
    {
        public string AssignmentVersion { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
    }
}
