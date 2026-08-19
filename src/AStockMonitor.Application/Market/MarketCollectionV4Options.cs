namespace AStockMonitor.Application.Market;

/// <summary>行情采集V4：官方K线主动拉取、全市场快照和重点Tick订阅。</summary>
public sealed class MarketCollectionV4Options
{
    public bool Enabled { get; set; }
    public OfficialBarPullOptions OfficialBars { get; set; } = new();
    public SnapshotPollingOptions Snapshot { get; set; } = new();
    public HotTickOptions HotTick { get; set; } = new();
}

public sealed class OfficialBarPullOptions
{
    public bool Enabled { get; set; }
    public int MaxWorkers { get; set; } = 6;
    public int SymbolsPerPartition { get; set; } = 500;
    public int RequestBatchSize { get; set; } = 50;
    public int FiveMinuteGraceSeconds { get; set; } = 15;
    public int ThirtyMinuteGraceSeconds { get; set; } = 20;
    public int SixtyMinuteGraceSeconds { get; set; } = 30;
    public TimeOnly DailyStartTime { get; set; } = new(15, 5);
    public int MaxRetries { get; set; } = 5;
    public int BarrierPollSeconds { get; set; } = 2;
    public int BarrierTimeoutSeconds { get; set; } = 240;
}

public sealed class SnapshotPollingOptions
{
    public bool Enabled { get; set; }
    public int TargetCycleSeconds { get; set; } = 5;
    public int WarnCycleSeconds { get; set; } = 10;
    public int StaleQuoteSeconds { get; set; } = 15;
    public decimal MinimumCoveragePercent { get; set; } = 99m;
    public int LeaderLeaseSeconds { get; set; } = 15;
}

public sealed class HotTickOptions
{
    public bool Enabled { get; set; }
    public int MaxWorkers { get; set; } = 6;
    public int SymbolsPerWorker { get; set; } = 50;
    public int MaxSymbols { get; set; } = 300;
    /// <summary>为当日新晋级到成立阶段的对子股票预留的订阅名额。</summary>
    public int IntradayReserveSymbols { get; set; } = 60;
    /// <summary>上一交易日基础池重新从MySQL生成的间隔，避免每10秒扫描历史命中。</summary>
    public int BaseRefreshSeconds { get; set; } = 300;
    public int AssignmentDebounceSeconds { get; set; } = 10;
    public int MaxGapRecoverySeconds { get; set; } = 120;
}
