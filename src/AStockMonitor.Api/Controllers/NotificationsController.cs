using AStockMonitor.Api.Models;
using AStockMonitor.Api.Services;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

/// <summary>查询、补拉和更新网页策略/对子任务卡。</summary>
[ApiController]
[Route("api/notifications")]
[Produces("application/json")]
[Tags("网页任务通知")]
public sealed class NotificationsController(IMySqlConnectionFactory connectionFactory) : ControllerBase
{
    /// <summary>分页查询任务卡；业务生命周期与用户处理状态相互独立。</summary>
    [HttpGet]
    [ProducesResponseType<NotificationPageDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<NotificationPageDto>> Get(
        int page = 1,
        int pageSize = 30,
        string? taskType = null,
        string? symbol = null,
        string? businessStatus = null,
        string? userStatus = "active",
        bool? isRead = null,
        bool? isStarred = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        Add(conditions, parameters, "task_type", "TaskType", Normalize(taskType));
        Add(conditions, parameters, "symbol", "Symbol", Normalize(symbol)?.ToUpperInvariant());
        Add(conditions, parameters, "business_status", "BusinessStatus", Normalize(businessStatus)?.ToLowerInvariant());
        Add(conditions, parameters, "user_status", "UserStatus", Normalize(userStatus)?.ToLowerInvariant());
        Add(conditions, parameters, "is_read", "IsRead", isRead);
        Add(conditions, parameters, "is_starred", "IsStarred", isStarred);
        var where = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
        parameters.Add("Offset", (page - 1) * pageSize);
        parameters.Add("PageSize", pageSize);

        await using var connection = connectionFactory.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM notification_task{where};", parameters,
            cancellationToken: cancellationToken));
        var items = (await connection.QueryAsync<NotificationTaskDto>(new CommandDefinition(
            NotificationProjectionWorker.TaskSelectSql + where +
            " ORDER BY FIELD(severity,'level1','critical','focus','observe','top','bottom','candidate','resolved','normal')," +
            " last_seen_at DESC,id DESC LIMIT @PageSize OFFSET @Offset;",
            parameters, cancellationToken: cancellationToken))).AsList();
        var watermark = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(MAX(id),0) FROM notification_task_change;",
            cancellationToken: cancellationToken));
        var totalPages = total == 0 ? 0 : (long)Math.Ceiling((decimal)total / pageSize);
        return Ok(new NotificationPageDto(page, pageSize, total, totalPages, watermark, items));
    }

    /// <summary>按单调变化水位补拉 SignalR 断线期间遗漏的任务。</summary>
    [HttpGet("changes")]
    [ProducesResponseType<NotificationChangePageDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<NotificationChangePageDto>> GetChanges(
        long afterId = 0,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        afterId = Math.Max(0, afterId);
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = connectionFactory.Create();
        var rows = (await connection.QueryAsync<ChangeRow>(new CommandDefinition(
            """
            SELECT c.id ChangeId,c.change_type ChangeType,c.event_id ChangeEventId,
                   c.revision ChangeRevision,c.occurred_at ChangeOccurredAt,
                   t.id Id,t.task_key TaskKey,t.task_type TaskType,t.source_id SourceId,
                   t.symbol Symbol,t.symbol_name SymbolName,t.severity Severity,
                   t.business_status BusinessStatus,t.revision Revision,
                   t.latest_event_id LatestEventId,t.title Title,t.summary Summary,
                   CAST(t.payload_json AS CHAR) PayloadJson,t.is_read IsRead,
                   t.is_starred IsStarred,t.user_status UserStatus,
                   t.first_seen_at FirstSeenAt,t.last_seen_at LastSeenAt,
                   t.read_at ReadAt,t.handled_at HandledAt,t.archived_at ArchivedAt,
                   t.created_at CreatedAt,t.updated_at UpdatedAt
            FROM notification_task_change c
            JOIN notification_task t ON t.id=c.task_id
            WHERE c.id>@AfterId ORDER BY c.id LIMIT @Limit;
            """, new { AfterId = afterId, Limit = limit + 1 },
            cancellationToken: cancellationToken))).AsList();
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var items = rows.Select(static row => new NotificationChangeDto(
            row.ChangeId, row.ChangeType, row.ChangeEventId, row.ChangeRevision,
            row.ChangeOccurredAt, row.ToTask())).ToArray();
        var watermark = items.Length == 0 ? afterId : items[^1].ChangeId;
        return Ok(new NotificationChangePageDto(afterId, watermark, hasMore, items));
    }

    /// <summary>查询一张任务卡。</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<NotificationTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NotificationTaskDto>> GetById(
        long id,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        var task = await connection.QuerySingleOrDefaultAsync<NotificationTaskDto>(new CommandDefinition(
            NotificationProjectionWorker.TaskSelectSql + " WHERE id=@Id;", new { Id = id },
            cancellationToken: cancellationToken));
        return task is null ? NotFound() : Ok(task);
    }

    /// <summary>更新已读、收藏、处理或归档状态，不改变策略/对子算法生命周期。</summary>
    [HttpPatch("{id:long}/state")]
    [ProducesResponseType<NotificationTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NotificationTaskDto>> UpdateState(
        long id,
        [FromBody] UpdateNotificationStateRequest request,
        CancellationToken cancellationToken)
    {
        var status = Normalize(request.UserStatus)?.ToLowerInvariant();
        if (status is not null && status is not ("active" or "handled" or "archived"))
        {
            return BadRequest(new { message = "userStatus只支持active、handled、archived" });
        }

        await using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE notification_task
            SET is_read=COALESCE(@IsRead,is_read),
                is_starred=COALESCE(@IsStarred,is_starred),
                user_status=COALESCE(@UserStatus,user_status),
                read_at=CASE WHEN @IsRead=TRUE THEN COALESCE(read_at,CURRENT_TIMESTAMP(6))
                             WHEN @IsRead=FALSE THEN NULL ELSE read_at END,
                handled_at=CASE WHEN @UserStatus='handled' THEN CURRENT_TIMESTAMP(6)
                                WHEN @UserStatus='active' THEN NULL ELSE handled_at END,
                archived_at=CASE WHEN @UserStatus='archived' THEN CURRENT_TIMESTAMP(6)
                                 WHEN @UserStatus='active' THEN NULL ELSE archived_at END
            WHERE id=@Id;
            """, new { Id = id, request.IsRead, request.IsStarred, UserStatus = status },
            cancellationToken: cancellationToken));
        if (affected == 0)
        {
            return NotFound();
        }
        var task = await connection.QuerySingleAsync<NotificationTaskDto>(new CommandDefinition(
            NotificationProjectionWorker.TaskSelectSql + " WHERE id=@Id;", new { Id = id },
            cancellationToken: cancellationToken));
        return Ok(task);
    }

    /// <summary>将当前筛选范围内的任务批量标记为已读。</summary>
    [HttpPost("read-all")]
    public async Task<ActionResult<object>> ReadAll(
        [FromQuery] string? taskType = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE notification_task
            SET is_read=TRUE,read_at=COALESCE(read_at,CURRENT_TIMESTAMP(6))
            WHERE is_read=FALSE AND (@TaskType IS NULL OR task_type=@TaskType);
            """, new { TaskType = Normalize(taskType) }, cancellationToken: cancellationToken));
        return Ok(new { affected });
    }

    private static void Add<T>(
        ICollection<string> conditions,
        DynamicParameters parameters,
        string column,
        string parameter,
        T? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        conditions.Add($"{column}=@{parameter}");
        parameters.Add(parameter, value);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class ChangeRow : NotificationTaskDto
    {
        public long ChangeId { get; init; }
        public string ChangeType { get; init; } = string.Empty;
        public string ChangeEventId { get; init; } = string.Empty;
        public int ChangeRevision { get; init; }
        public DateTime ChangeOccurredAt { get; init; }

        public NotificationTaskDto ToTask() => new()
        {
            Id = Id, TaskKey = TaskKey, TaskType = TaskType, SourceId = SourceId,
            Symbol = Symbol, SymbolName = SymbolName, Severity = Severity,
            BusinessStatus = BusinessStatus, Revision = Revision, LatestEventId = LatestEventId,
            Title = Title, Summary = Summary, PayloadJson = PayloadJson, IsRead = IsRead,
            IsStarred = IsStarred, UserStatus = UserStatus, FirstSeenAt = FirstSeenAt,
            LastSeenAt = LastSeenAt, ReadAt = ReadAt, HandledAt = HandledAt,
            ArchivedAt = ArchivedAt, CreatedAt = CreatedAt, UpdatedAt = UpdatedAt
        };
    }
}
