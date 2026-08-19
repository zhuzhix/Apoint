namespace AStockMonitor.Api.Models;

/// <summary>浏览器任务卡的稳定外层契约。</summary>
public class NotificationTaskDto
{
    public int SchemaVersion { get; init; } = 1;
    public long Id { get; init; }
    public string TaskKey { get; init; } = string.Empty;
    public string TaskType { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string? Symbol { get; init; }
    public string? SymbolName { get; init; }
    public string Severity { get; init; } = string.Empty;
    public string BusinessStatus { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string LatestEventId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = "{}";
    public bool IsRead { get; init; }
    public bool IsStarred { get; init; }
    public string UserStatus { get; init; } = string.Empty;
    public DateTime FirstSeenAt { get; init; }
    public DateTime LastSeenAt { get; init; }
    public DateTime? ReadAt { get; init; }
    public DateTime? HandledAt { get; init; }
    public DateTime? ArchivedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>带单调水位的任务变化，用于浏览器断线补拉。</summary>
public sealed record NotificationChangeDto(
    long ChangeId,
    string ChangeType,
    string EventId,
    int Revision,
    DateTime OccurredAt,
    NotificationTaskDto Task);

public sealed record NotificationPageDto(
    int Page,
    int PageSize,
    long Total,
    long TotalPages,
    long HighWatermark,
    IReadOnlyList<NotificationTaskDto> Items);

public sealed record NotificationChangePageDto(
    long AfterId,
    long HighWatermark,
    bool HasMore,
    IReadOnlyList<NotificationChangeDto> Items);

/// <summary>用户对任务的操作状态；空字段表示保持原值。</summary>
public sealed class UpdateNotificationStateRequest
{
    public bool? IsRead { get; init; }
    public bool? IsStarred { get; init; }
    public string? UserStatus { get; init; }
}
