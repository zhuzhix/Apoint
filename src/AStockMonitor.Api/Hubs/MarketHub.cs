using AStockMonitor.Application.Market;
using Microsoft.AspNetCore.SignalR;

namespace AStockMonitor.Api.Hubs;

/// <summary>按股票代码管理 SignalR 行情推送分组。</summary>
public sealed class MarketHub : Hub
{
    /// <summary>订阅最多 500 个股票分组；空代码会被忽略。</summary>
    public async Task SubscribeSymbols(IReadOnlyCollection<string> symbols)
    {
        foreach (var symbol in symbols.Where(static s => !string.IsNullOrWhiteSpace(s)).Take(500))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, MarketSymbols.GroupName(symbol));
        }
    }

    /// <summary>取消订阅最多 500 个股票分组。</summary>
    public async Task UnsubscribeSymbols(IReadOnlyCollection<string> symbols)
    {
        foreach (var symbol in symbols.Where(static s => !string.IsNullOrWhiteSpace(s)).Take(500))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, MarketSymbols.GroupName(symbol));
        }
    }
}
