namespace AStockMonitor.Application.Market;

public static class MarketSymbols
{
    public static string Normalize(string symbol) => symbol.Trim().ToUpperInvariant();

    public static string GroupName(string symbol) => $"market:symbol:{Normalize(symbol).Replace('.', '_')}";
}
