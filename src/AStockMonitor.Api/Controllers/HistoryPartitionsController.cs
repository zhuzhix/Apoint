using System.Net;
using System.Text.Json;
using Dapper;
using AStockMonitor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

/// <summary>查询历史 K 线下载批次和独立分区，并提交可审计的人工重试命令。</summary>
[ApiController]
[Route("api/history")]
[Produces("application/json")]
[Tags("历史K线分区调度")]
public sealed class HistoryPartitionsController(
    IMySqlConnectionFactory connectionFactory,
    IConfiguration configuration) : ControllerBase
{
    private static readonly HashSet<string> BatchStatuses =
        ["running", "complete", "partial", "failed"];
    private static readonly HashSet<string> PartitionStatuses =
    [
        "pending", "running", "retry_waiting", "complete",
        "retry_exhausted", "failed_permanent", "cancelled"
    ];

    /// <summary>分页查询历史 K 线下载批次。</summary>
    /// <param name="page">页码，从1开始。</param>
    /// <param name="pageSize">每页数量，范围1～200。</param>
    /// <param name="status">可选批次状态：running、complete、partial、failed。</param>
    /// <param name="dateFrom">任务开始日期下界。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpGet("batches")]
    [ProducesResponseType(typeof(PagedResponse<HistoryBatchItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResponse<HistoryBatchItem>>> GetBatches(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] DateTime? dateFrom = null,
        CancellationToken cancellationToken = default)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        status = NormalizeStatus(status, BatchStatuses);
        if (status == InvalidStatus)
        {
            return ValidationProblem("不支持的批次状态。");
        }

        var where = "WHERE (@Status IS NULL OR b.status=@Status) " +
                    "AND (@DateFrom IS NULL OR b.started_at>=@DateFrom)";
        var parameters = new
        {
            Status = status,
            DateFrom = dateFrom,
            Offset = (page - 1) * pageSize,
            PageSize = pageSize
        };
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM bar_ingest_batch b {where};", parameters,
            commandTimeout: 3, cancellationToken: cancellationToken));
        var items = (await connection.QueryAsync<HistoryBatchItem>(new CommandDefinition(
            $$"""
            SELECT b.id AS Id, b.batch_key AS BatchKey, b.job_type AS JobType,
                   b.date_from AS DateFrom, b.date_to AS DateTo,
                   b.frequencies AS Frequencies, b.status AS Status,
                   b.requested_symbols AS RequestedSymbols,
                   b.completed_symbols AS CompletedSymbols,
                   b.rows_read AS RowsRead, b.rows_written AS RowsWritten,
                   b.error_count AS ErrorCount, b.started_at AS StartedAt,
                   b.finished_at AS FinishedAt,
                   COUNT(p.partition_id) AS PartitionCount,
                   COALESCE(SUM(p.status='complete'),0) AS CompletePartitions,
                   COALESCE(SUM(p.status='running'),0) AS RunningPartitions,
                   COALESCE(SUM(p.status='retry_waiting'),0) AS RetryWaitingPartitions,
                   COALESCE(SUM(p.status IN ('retry_exhausted','failed_permanent')),0)
                       AS FailedPartitions,
                   COALESCE(SUM(p.attempt_count),0) AS AttemptCount
            FROM bar_ingest_batch b
            LEFT JOIN bar_ingest_partition p ON p.batch_id=b.id
            {{where}}
            GROUP BY b.id
            ORDER BY b.id DESC
            LIMIT @PageSize OFFSET @Offset;
            """, parameters, commandTimeout: 3, cancellationToken: cancellationToken))).ToArray();
        return Ok(new PagedResponse<HistoryBatchItem>(page, pageSize, total, items));
    }

    /// <summary>查询一个历史下载批次的汇总和健康状态。</summary>
    /// <param name="batchId">批次主键。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpGet("batches/{batchId:long}")]
    [ProducesResponseType(typeof(HistoryBatchDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HistoryBatchDetail>> GetBatch(
        long batchId, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var batch = await connection.QuerySingleOrDefaultAsync<HistoryBatchDetail>(
            new CommandDefinition(
            """
            SELECT b.id AS Id, b.batch_key AS BatchKey, b.job_type AS JobType,
                   b.date_from AS DateFrom, b.date_to AS DateTo,
                   b.frequencies AS Frequencies, b.status AS Status,
                   b.requested_symbols AS RequestedSymbols,
                   b.completed_symbols AS CompletedSymbols,
                   b.rows_read AS RowsRead, b.rows_written AS RowsWritten,
                   b.rows_filtered AS RowsFiltered, b.error_count AS ErrorCount,
                   b.started_at AS StartedAt, b.finished_at AS FinishedAt,
                   COUNT(p.partition_id) AS PartitionCount,
                   COALESCE(SUM(p.status='complete'),0) AS CompletePartitions,
                   COALESCE(SUM(p.status='running'),0) AS RunningPartitions,
                   COALESCE(SUM(p.status='pending'),0) AS PendingPartitions,
                   COALESCE(SUM(p.status='retry_waiting'),0) AS RetryWaitingPartitions,
                   COALESCE(SUM(p.status IN ('retry_exhausted','failed_permanent')),0)
                       AS FailedPartitions,
                   COALESCE(MAX(TIMESTAMPDIFF(SECOND,p.heartbeat_at,UTC_TIMESTAMP(6))),0)
                       AS MaxHeartbeatAgeSeconds,
                   COALESCE(MAX(TIMESTAMPDIFF(SECOND,p.progress_at,UTC_TIMESTAMP(6))),0)
                       AS MaxProgressAgeSeconds,
                   COALESCE(SUM(p.attempt_count),0) AS AttemptCount
            FROM bar_ingest_batch b
            LEFT JOIN bar_ingest_partition p ON p.batch_id=b.id
            WHERE b.id=@BatchId
            GROUP BY b.id;
            """, new { BatchId = batchId }, commandTimeout: 3,
            cancellationToken: cancellationToken));
        return batch is null ? NotFound() : Ok(batch);
    }

    /// <summary>分页查询指定批次的下载分区。</summary>
    /// <param name="batchId">批次主键。</param>
    /// <param name="page">页码，从1开始。</param>
    /// <param name="pageSize">每页数量，范围1～200。</param>
    /// <param name="status">可选分区状态。</param>
    /// <param name="sort">排序：index、heartbeatAgeDesc、progressAgeDesc、retryDesc。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpGet("batches/{batchId:long}/partitions")]
    [ProducesResponseType(typeof(PagedResponse<HistoryPartitionItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResponse<HistoryPartitionItem>>> GetPartitions(
        long batchId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null,
        [FromQuery] string sort = "index",
        CancellationToken cancellationToken = default)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        status = NormalizeStatus(status, PartitionStatuses);
        if (status == InvalidStatus)
        {
            return ValidationProblem("不支持的分区状态。");
        }

        var orderBy = sort.Trim().ToLowerInvariant() switch
        {
            "heartbeatagedesc" => "heartbeat_age_seconds DESC, partition_index",
            "progressagedesc" => "progress_age_seconds DESC, partition_index",
            "retrydesc" => "attempt_count DESC, partition_index",
            _ => "partition_index"
        };
        var parameters = new
        {
            BatchId = batchId,
            Status = status,
            Offset = (page - 1) * pageSize,
            PageSize = pageSize
        };
        var where = "WHERE batch_id=@BatchId AND (@Status IS NULL OR status=@Status)";
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM bar_ingest_partition {where};", parameters,
            commandTimeout: 3, cancellationToken: cancellationToken));
        var items = (await connection.QueryAsync<HistoryPartitionItem>(new CommandDefinition(
            $$"""
            SELECT partition_id AS PartitionId, partition_index AS PartitionIndex,
                   symbol_count AS SymbolCount, status AS Status, process_id AS ProcessId,
                   attempt_count AS AttemptCount, max_attempts AS MaxAttempts,
                   heartbeat_at AS HeartbeatAt,
                   COALESCE(TIMESTAMPDIFF(SECOND,heartbeat_at,UTC_TIMESTAMP(6)),0)
                       AS HeartbeatAgeSeconds,
                   progress_at AS ProgressAt,
                   COALESCE(TIMESTAMPDIFF(SECOND,progress_at,UTC_TIMESTAMP(6)),0)
                       AS ProgressAgeSeconds,
                   completed_tasks AS CompletedTasks, total_tasks AS TotalTasks,
                   rows_read AS RowsRead, rows_written AS RowsWritten,
                   rows_filtered AS RowsFiltered, last_symbol AS LastSymbol,
                   last_frequency AS LastFrequency, next_retry_at AS NextRetryAt,
                   failure_code AS FailureCode, last_error AS LastError,
                   started_at AS StartedAt, finished_at AS FinishedAt
            FROM bar_ingest_partition
            {{where}}
            ORDER BY {{orderBy}}
            LIMIT @PageSize OFFSET @Offset;
            """, parameters, commandTimeout: 3, cancellationToken: cancellationToken))).ToArray();
        return Ok(new PagedResponse<HistoryPartitionItem>(page, pageSize, total, items));
    }

    /// <summary>查询分区股票列表、断点摘要和每次进程执行记录。</summary>
    /// <param name="partitionId">分区唯一标识。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpGet("partitions/{partitionId}")]
    [ProducesResponseType(typeof(HistoryPartitionDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HistoryPartitionDetail>> GetPartition(
        string partitionId, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var partition = await connection.QuerySingleOrDefaultAsync<HistoryPartitionDetail>(
            new CommandDefinition(
            """
            SELECT partition_id AS PartitionId, batch_id AS BatchId,
                   partition_index AS PartitionIndex, symbol_count AS SymbolCount,
                   symbols_json AS SymbolsJson, status AS Status, process_id AS ProcessId,
                   attempt_count AS AttemptCount, max_attempts AS MaxAttempts,
                   heartbeat_at AS HeartbeatAt, progress_at AS ProgressAt,
                   COALESCE(TIMESTAMPDIFF(SECOND,heartbeat_at,UTC_TIMESTAMP(6)),0)
                       AS HeartbeatAgeSeconds,
                   COALESCE(TIMESTAMPDIFF(SECOND,progress_at,UTC_TIMESTAMP(6)),0)
                       AS ProgressAgeSeconds,
                   completed_tasks AS CompletedTasks, total_tasks AS TotalTasks,
                   rows_read AS RowsRead, rows_written AS RowsWritten,
                   rows_filtered AS RowsFiltered, next_retry_at AS NextRetryAt,
                   failure_code AS FailureCode, retryable AS Retryable,
                   last_error AS LastError, started_at AS StartedAt,
                   finished_at AS FinishedAt
            FROM bar_ingest_partition WHERE partition_id=@PartitionId;
            """, new { PartitionId = partitionId }, commandTimeout: 3,
            cancellationToken: cancellationToken));
        if (partition is null)
        {
            return NotFound();
        }

        partition.Attempts = (await connection.QueryAsync<HistoryPartitionAttempt>(
            new CommandDefinition(
            """
            SELECT id AS Id, attempt_number AS AttemptNumber,
                   owner_instance_id AS OwnerInstanceId, process_id AS ProcessId,
                   status AS Status, started_at AS StartedAt,
                   heartbeat_at AS HeartbeatAt, progress_at AS ProgressAt,
                   finished_at AS FinishedAt, rows_read AS RowsRead,
                   rows_written AS RowsWritten, completed_tasks AS CompletedTasks,
                   failure_code AS FailureCode, error_message AS ErrorMessage
            FROM bar_ingest_partition_attempt
            WHERE partition_id=@PartitionId
            ORDER BY attempt_number DESC;
            """, new { PartitionId = partitionId }, commandTimeout: 3,
            cancellationToken: cancellationToken))).ToArray();
        partition.Symbols = JsonSerializer.Deserialize<string[]>(partition.SymbolsJson) ?? [];
        return Ok(partition);
    }

    /// <summary>提交单个失败分区的人工重试命令。</summary>
    /// <remarks>API不直接控制Windows进程。命令持久化后由Python调度器领取并从原checkpoint续传。</remarks>
    /// <param name="partitionId">分区唯一标识。</param>
    /// <param name="request">幂等请求ID和操作理由。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpPost("partitions/{partitionId}/retry")]
    [ProducesResponseType(typeof(HistoryCommandAccepted), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<HistoryCommandAccepted>> RetryPartition(
        string partitionId,
        [FromBody] RetryHistoryPartitionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsOperationsAuthorized())
        {
            return Unauthorized();
        }
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reason))
        {
            return ValidationProblem("requestId和reason不能为空。");
        }

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await connection.QuerySingleOrDefaultAsync<HistoryCommandAccepted>(
            new CommandDefinition(
                "SELECT id AS CommandId, request_id AS RequestId, status AS Status " +
                "FROM history_control_command WHERE request_id=@RequestId;",
                new { RequestId = request.RequestId.ToString() }, transaction,
                commandTimeout: 3, cancellationToken: cancellationToken));
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return Accepted(existing);
        }

        var partition = await connection.QuerySingleOrDefaultAsync<PartitionCommandTarget>(
            new CommandDefinition(
                "SELECT batch_id AS BatchId,status AS Status FROM bar_ingest_partition " +
                "WHERE partition_id=@PartitionId FOR UPDATE;",
                new { PartitionId = partitionId }, transaction, commandTimeout: 3,
                cancellationToken: cancellationToken));
        if (partition is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return NotFound();
        }
        if (partition.Status == "running")
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new ProblemDetails
            {
                Title = "分区正在运行",
                Detail = "运行中的分区不能创建并行重试。"
            });
        }

        var commandId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO history_control_command
                (request_id,command_type,batch_id,partition_id,reason,
                 requested_by,requested_from,status)
            VALUES (@RequestId,'retry_partition',@BatchId,@PartitionId,@Reason,
                    @RequestedBy,@RequestedFrom,'pending');
            SELECT LAST_INSERT_ID();
            """, new
            {
                RequestId = request.RequestId.ToString(),
                partition.BatchId,
                PartitionId = partitionId,
                Reason = request.Reason.Trim(),
                RequestedBy = User.Identity?.Name ?? "local-operator",
                RequestedFrom = HttpContext.Connection.RemoteIpAddress?.ToString()
            }, transaction, commandTimeout: 3, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return Accepted(new HistoryCommandAccepted(commandId, request.RequestId, "pending"));
    }

    /// <summary>为一个批次中所有终态失败分区提交批量重试命令。</summary>
    /// <remarks>单次最多接受100个失败分区；命令由Python调度器异步领取。</remarks>
    /// <param name="batchId">批次主键。</param>
    /// <param name="request">幂等请求ID和操作理由。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpPost("batches/{batchId:long}/retry-failed")]
    [ProducesResponseType(typeof(HistoryCommandAccepted), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<HistoryCommandAccepted>> RetryFailedBatch(
        long batchId,
        [FromBody] RetryHistoryPartitionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsOperationsAuthorized()) return Unauthorized();
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reason))
        {
            return ValidationProblem("requestId和reason不能为空。");
        }

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await connection.QuerySingleOrDefaultAsync<HistoryCommandAccepted>(
            new CommandDefinition(
                "SELECT id AS CommandId,request_id AS RequestId,status AS Status " +
                "FROM history_control_command WHERE request_id=@RequestId;",
                new { RequestId = request.RequestId.ToString() }, transaction,
                commandTimeout: 3, cancellationToken: cancellationToken));
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return Accepted(existing);
        }

        var batchExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM bar_ingest_batch WHERE id=@BatchId);",
            new { BatchId = batchId }, transaction, commandTimeout: 3,
            cancellationToken: cancellationToken));
        if (!batchExists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return NotFound();
        }
        var failedCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*) FROM bar_ingest_partition
            WHERE batch_id=@BatchId
              AND status IN ('retry_exhausted','failed_permanent');
            """, new { BatchId = batchId }, transaction, commandTimeout: 3,
            cancellationToken: cancellationToken));
        if (failedCount == 0 || failedCount > 100)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new ProblemDetails
            {
                Title = failedCount == 0 ? "没有可重试分区" : "失败分区超过单次上限",
                Detail = failedCount == 0
                    ? "该批次不存在retry_exhausted或failed_permanent分区。"
                    : $"当前有{failedCount}个失败分区，单次最多100个。"
            });
        }

        var commandId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO history_control_command
                (request_id,command_type,batch_id,reason,requested_by,requested_from,status)
            VALUES (@RequestId,'retry_failed_batch',@BatchId,@Reason,
                    @RequestedBy,@RequestedFrom,'pending');
            SELECT LAST_INSERT_ID();
            """, new
            {
                RequestId = request.RequestId.ToString(), BatchId = batchId,
                Reason = request.Reason.Trim(),
                RequestedBy = User.Identity?.Name ?? "local-operator",
                RequestedFrom = HttpContext.Connection.RemoteIpAddress?.ToString()
            }, transaction, commandTimeout: 3, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return Accepted(new HistoryCommandAccepted(commandId, request.RequestId, "pending"));
    }

    private bool IsOperationsAuthorized()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is not null && IPAddress.IsLoopback(remote))
        {
            return true;
        }
        var expected = configuration["HistoryOperations:ApiKey"];
        return !string.IsNullOrWhiteSpace(expected) &&
               Request.Headers.TryGetValue("X-AStock-Operations-Key", out var supplied) &&
               string.Equals(expected, supplied.ToString(), StringComparison.Ordinal);
    }

    private const string InvalidStatus = "__invalid__";
    private static string? NormalizeStatus(string? status, HashSet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var normalized = status.Trim().ToLowerInvariant();
        return allowed.Contains(normalized) ? normalized : InvalidStatus;
    }

    private static (int Page, int PageSize) NormalizePage(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));

    public sealed record PagedResponse<T>(int Page, int PageSize, long Total, IReadOnlyList<T> Items);
    public sealed record RetryHistoryPartitionRequest(Guid RequestId, string Reason);
    public sealed record HistoryCommandAccepted(long CommandId, Guid RequestId, string Status);
    private sealed record PartitionCommandTarget(long BatchId, string Status);

    public class HistoryBatchItem
    {
        public long Id { get; init; }
        public string BatchKey { get; init; } = "";
        public string JobType { get; init; } = "";
        public DateTime DateFrom { get; init; }
        public DateTime DateTo { get; init; }
        public string Frequencies { get; init; } = "";
        public string Status { get; init; } = "";
        public int RequestedSymbols { get; init; }
        public int CompletedSymbols { get; init; }
        public long RowsRead { get; init; }
        public long RowsWritten { get; init; }
        public int ErrorCount { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? FinishedAt { get; init; }
        public int PartitionCount { get; init; }
        public int CompletePartitions { get; init; }
        public int RunningPartitions { get; init; }
        public int RetryWaitingPartitions { get; init; }
        public int FailedPartitions { get; init; }
        public int AttemptCount { get; init; }
        public decimal ProgressPercent => PartitionCount == 0 ? 100 :
            Math.Round((decimal)CompletePartitions * 100 / PartitionCount, 2);
    }

    public sealed class HistoryBatchDetail : HistoryBatchItem
    {
        public long RowsFiltered { get; init; }
        public int PendingPartitions { get; init; }
        public long MaxHeartbeatAgeSeconds { get; init; }
        public long MaxProgressAgeSeconds { get; init; }
    }

    public class HistoryPartitionItem
    {
        public string PartitionId { get; init; } = "";
        public int PartitionIndex { get; init; }
        public int SymbolCount { get; init; }
        public string Status { get; init; } = "";
        public int? ProcessId { get; init; }
        public int AttemptCount { get; init; }
        public int MaxAttempts { get; init; }
        public DateTime? HeartbeatAt { get; init; }
        public long HeartbeatAgeSeconds { get; init; }
        public DateTime? ProgressAt { get; init; }
        public long ProgressAgeSeconds { get; init; }
        public int CompletedTasks { get; init; }
        public int TotalTasks { get; init; }
        public decimal ProgressPercent => TotalTasks == 0 ? 0 :
            Math.Round((decimal)CompletedTasks * 100 / TotalTasks, 2);
        public long RowsRead { get; init; }
        public long RowsWritten { get; init; }
        public long RowsFiltered { get; init; }
        public string? LastSymbol { get; init; }
        public string? LastFrequency { get; init; }
        public DateTime? NextRetryAt { get; init; }
        public string? FailureCode { get; init; }
        public string? LastError { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? FinishedAt { get; init; }
    }

    public sealed class HistoryPartitionDetail : HistoryPartitionItem
    {
        public long BatchId { get; init; }
        public string SymbolsJson { get; init; } = "[]";
        public IReadOnlyList<string> Symbols { get; set; } = [];
        public bool Retryable { get; init; }
        public IReadOnlyList<HistoryPartitionAttempt> Attempts { get; set; } = [];
    }

    public sealed class HistoryPartitionAttempt
    {
        public long Id { get; init; }
        public int AttemptNumber { get; init; }
        public string OwnerInstanceId { get; init; } = "";
        public int? ProcessId { get; init; }
        public string Status { get; init; } = "";
        public DateTime StartedAt { get; init; }
        public DateTime HeartbeatAt { get; init; }
        public DateTime ProgressAt { get; init; }
        public DateTime? FinishedAt { get; init; }
        public long RowsRead { get; init; }
        public long RowsWritten { get; init; }
        public int CompletedTasks { get; init; }
        public string? FailureCode { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
