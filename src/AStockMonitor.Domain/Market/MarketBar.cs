namespace AStockMonitor.Domain.Market;

/// <summary>系统统一使用的官方 K 线模型。</summary>
public sealed record MarketBar(
    string Symbol,
    string Frequency,
    DateOnly TradingDate,
    DateTimeOffset Bob,
    DateTimeOffset Eob,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    decimal? PreClose,
    long Volume,
    decimal Amount,
    bool IsClosed,
    bool IsVolumeComplete,
    bool IsAmountComplete,
    int Revision,
    string Source,
    bool OfficialConfirmed,
    DateTimeOffset FirstTickTime,
    DateTimeOffset LastTickTime,
    string RowHash,
    int SourcePriority = 200,
    long? RecoveryRunId = null,
    bool IsReplay = false,
    string QualityStatus = "unchecked",
    DateTimeOffset? RecoveredAt = null,
    DateTimeOffset? SourceUpdatedAt = null);

/// <summary>正式 K 线支持的标准周期。</summary>
public static class MarketBarFrequencies
{
    public const string Minute5 = "5m";
    public const string Minute30 = "30m";
    public const string Minute60 = "60m";
    public const string Daily = "1d";

    public static readonly IReadOnlyList<string> All =
        [Minute5, Minute30, Minute60, Daily];

    public static bool IsSupported(string frequency) =>
        All.Contains(frequency, StringComparer.OrdinalIgnoreCase);
}
