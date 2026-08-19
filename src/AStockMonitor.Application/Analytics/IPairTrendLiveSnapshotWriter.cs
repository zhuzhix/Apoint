using AStockMonitor.Domain.Analytics;

namespace AStockMonitor.Application.Analytics;

/// <summary>
/// 将 API 内存中一次完整回放的对子结果投影到实时查询表。
/// 原始 K 线不通过该接口落库；持久化内容仅限对子事件、命中和生命周期审计。
/// </summary>
public interface IPairTrendLiveSnapshotWriter
{
    Task WriteAsync(
        DateOnly tradingDate,
        string sourceCycleId,
        PairTrendSymbolResult result,
        CancellationToken cancellationToken = default);
}
