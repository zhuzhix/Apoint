using Microsoft.AspNetCore.SignalR;

namespace AStockMonitor.Api.Hubs;

/// <summary>按股票代码订阅策略机会变化。</summary>
public sealed class StrategyHub : Hub
{
    public async Task SubscribeSymbols(IReadOnlyCollection<string> symbols)
    {
        foreach (var symbol in symbols.Where(static x => !string.IsNullOrWhiteSpace(x)).Take(500))
            await Groups.AddToGroupAsync(Context.ConnectionId, Group(symbol));
    }

    public async Task UnsubscribeSymbols(IReadOnlyCollection<string> symbols)
    {
        foreach (var symbol in symbols.Where(static x => !string.IsNullOrWhiteSpace(x)).Take(500))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(symbol));
    }

    public static string Group(string symbol) => $"strategy:{symbol.Trim().ToUpperInvariant()}";
}
