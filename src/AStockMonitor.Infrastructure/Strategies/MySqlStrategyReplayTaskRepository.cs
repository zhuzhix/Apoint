using AStockMonitor.Application.Strategies;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Infrastructure.Strategies;

public sealed class MySqlStrategyReplayTaskRepository(
    IMySqlConnectionFactory connectionFactory) : IStrategyReplayTaskRepository
{
    public async Task<StrategyReplayTaskWork?> TryClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT task_id TaskId, symbol Symbol, date_from DateFrom, date_to DateTo,
                   source_command_id SourceCommandId, attempt_count AttemptCount
            FROM strategy_replay_task
            WHERE status='pending'
            ORDER BY created_at, task_id
            LIMIT 1 FOR UPDATE SKIP LOCKED;
            """, transaction: transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE strategy_replay_task
            SET status='running', attempt_count=attempt_count+1, started_at=UTC_TIMESTAMP(6), last_error=NULL
            WHERE task_id=@TaskId;
            """, new { row.TaskId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new StrategyReplayTaskWork(
            Guid.Parse(row.TaskId), row.Symbol, DateOnly.FromDateTime(row.DateFrom),
            DateOnly.FromDateTime(row.DateTo), Guid.Parse(row.SourceCommandId), row.AttemptCount + 1);
    }

    public async Task CompleteAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE strategy_replay_task
            SET status='completed', completed_at=UTC_TIMESTAMP(6), last_error=NULL
            WHERE task_id=@TaskId AND status='running';
            """, new { TaskId = taskId.ToString() }, cancellationToken: cancellationToken));
    }

    public async Task FailAsync(Guid taskId, string error, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE strategy_replay_task
            SET status=IF(attempt_count >= 5,'failed','pending'), last_error=@Error
            WHERE task_id=@TaskId AND status='running';
            """, new { TaskId = taskId.ToString(), Error = error.Length <= 1024 ? error : error[..1024] },
            cancellationToken: cancellationToken));
    }

    private sealed class Row
    {
        public string TaskId { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public DateTime DateFrom { get; init; }
        public DateTime DateTo { get; init; }
        public string SourceCommandId { get; init; } = string.Empty;
        public int AttemptCount { get; init; }
    }
}
