using System.Text.Json;

namespace AStockMonitor.Contracts.Market;

/// <summary>V2 正式 K 线生命周期事件中的 OHLCV 数据。</summary>
public sealed record BarLifecyclePayloadV2(
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal? PreClose,
    long Volume,
    decimal Amount);

/// <summary>
/// 官方 K 线在 MySQL 提交后发布的唯一 V2 事件契约。
/// 生产者和消费者都必须使用本类型，禁止以匿名对象拼装可靠事件。
/// </summary>
public sealed record BarLifecycleEventV2(
    int SchemaVersion,
    string EventId,
    string EventType,
    string Symbol,
    string Frequency,
    DateOnly TradingDate,
    DateTimeOffset Bob,
    DateTimeOffset Eob,
    int Revision,
    string RowHash,
    string Source,
    DateTimeOffset SourceUpdatedAt,
    bool OfficialConfirmed,
    string CollectionMode,
    long? RecoveryRunId,
    DateTimeOffset OccurredAt,
    BarLifecyclePayloadV2 Bar)
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>校验可靠事件的结构和正式四周期约束。</summary>
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported bar event schema version: {SchemaVersion}.");
        if (EventType is not ("BarClosed" or "BarRevised"))
            throw new InvalidDataException($"Unsupported reliable bar event type: {EventType}.");
        if (Frequency is not ("5m" or "30m" or "60m" or "1d"))
            throw new InvalidDataException($"Unsupported official bar frequency: {Frequency}.");
        if (string.IsNullOrWhiteSpace(EventId) || string.IsNullOrWhiteSpace(Symbol) ||
            string.IsNullOrWhiteSpace(RowHash) || string.IsNullOrWhiteSpace(Source))
            throw new InvalidDataException("Bar event identity and provenance fields are required.");
        if (!OfficialConfirmed)
            throw new InvalidDataException("Reliable V2 bar events must be officially confirmed.");
        if (Revision < 0 || Bar.Open <= 0 || Bar.High <= 0 || Bar.Low <= 0 || Bar.Close <= 0 ||
            Bar.High < Math.Max(Bar.Open, Bar.Close) ||
            Bar.Low > Math.Min(Bar.Open, Bar.Close) || Bar.High < Bar.Low ||
            Bar.Volume < 0 || Bar.Amount < 0)
            throw new InvalidDataException("Bar event failed revision/OHLCV validation.");
    }
}

/// <summary>统一的 V2 Bar 事件 JSON 设置和反序列化入口。</summary>
public static class BarLifecycleEventV2Json
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static string Serialize(BarLifecycleEventV2 value)
    {
        value.Validate();
        return JsonSerializer.Serialize(value, Options);
    }

    public static BarLifecycleEventV2 Deserialize(string payload)
    {
        var value = JsonSerializer.Deserialize<BarLifecycleEventV2>(payload, Options)
                    ?? throw new InvalidDataException("Bar event payload is empty or invalid.");
        value.Validate();
        return value;
    }
}

/// <summary>
/// 可靠 Bar 事件的前端通知资格。历史补数仍由业务消费者落库和重建状态，
/// 但超过实时窗口的迟到事件不得伪装成盘中即时提醒。
/// </summary>
public static class BarEventDeliveryPolicy
{
    /// <summary>判断事件对应的 K 线结束时间是否仍属于可通知窗口。</summary>
    public static bool IsLiveNotificationEligible(
        BarLifecycleEventV2 value,
        DateTimeOffset now)
    {
        var age = now - value.Eob;
        if (age < TimeSpan.FromMinutes(-2))
            return false;

        var maximumAge = value.Frequency == "1d"
            ? TimeSpan.FromHours(2)
            : TimeSpan.FromHours(1);
        return age <= maximumAge;
    }
}
