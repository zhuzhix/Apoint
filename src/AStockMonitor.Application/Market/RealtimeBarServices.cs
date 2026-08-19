using AStockMonitor.Domain.Market;

namespace AStockMonitor.Application.Market;

/// <summary>
/// Reads canonical bars whose source of truth is the DongCai GM SDK. Business
/// callers use this boundary instead of depending on physical K-line tables.
/// </summary>
public interface IOfficialBarReader
{
    Task<MarketBar?> GetLatestAsync(
        string symbol,
        string frequency,
        bool includeActive,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<MarketBar>> GetBarsAsync(
        string symbol,
        string frequency,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken);
}
