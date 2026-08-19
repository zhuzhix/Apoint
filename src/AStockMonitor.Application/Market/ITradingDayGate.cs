namespace AStockMonitor.Application.Market;

/// <summary>
/// 统一交易日门禁。所有盘中自动任务均使用同一份按日股票池判定，
/// 避免不同服务分别按星期、时间或第三方日历作出不一致结论。
/// </summary>
public interface ITradingDayGate
{
    /// <summary>指定日期是否存在至少一只可交易的沪深非ST股票。</summary>
    Task<bool> IsTradingDayAsync(DateOnly tradingDate, CancellationToken cancellationToken = default);
}
