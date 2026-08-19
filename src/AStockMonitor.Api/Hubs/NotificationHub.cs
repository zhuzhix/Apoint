using Microsoft.AspNetCore.SignalR;

namespace AStockMonitor.Api.Hubs;

/// <summary>向浏览器推送全市场低频业务任务，不承载 Tick 或全市场原始行情。</summary>
public sealed class NotificationHub : Hub
{
    /// <summary>所有连接自动加入业务任务组，重连后由服务端重新加入。</summary>
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, AllTasksGroup);
        await base.OnConnectedAsync();
    }

    public const string AllTasksGroup = "notifications:all";
}

