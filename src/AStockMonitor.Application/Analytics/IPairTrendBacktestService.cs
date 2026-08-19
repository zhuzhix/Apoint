using AStockMonitor.Domain.Analytics;

namespace AStockMonitor.Application.Analytics;

/// <summary>对子趋势历史回测应用服务。</summary>
public interface IPairTrendBacktestService
{
    /// <summary>执行、续跑或复用一次由稳定 run_key 标识的历史回测。</summary>
    Task<PairTrendBacktestResult> RunAsync(
        PairTrendBacktestRequest request,
        CancellationToken cancellationToken = default);
}
