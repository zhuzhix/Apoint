using AStockMonitor.Domain.Market;

namespace AStockMonitor.Application.Market;

/// <summary>
/// Stable business-facing market data boundary. Callers do not need to know
/// whether a result came from process memory, Redis, or MySQL.
/// </summary>
public interface IMarketDataReader
{
    Task<LatestQuote?> GetLatestAsync(string symbol, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, LatestQuote>> GetLatestBatchAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<TickEvent>> GetRecentTicksAsync(
        string symbol,
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed class MarketMemoryOptions
{
    /// <summary>Maximum raw Tick records retained per symbol in L0 memory.</summary>
    public int RecentTicksPerSymbol { get; set; } = 256;
}
