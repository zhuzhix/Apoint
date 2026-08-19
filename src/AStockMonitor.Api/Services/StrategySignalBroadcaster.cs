using System.Text.Json;
using AStockMonitor.Api.Hubs;
using AStockMonitor.Infrastructure.Market;
using AStockMonitor.Infrastructure.Observability;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace AStockMonitor.Api.Services;

/// <summary>可靠消费策略事件Stream并向已订阅浏览器推送变化。</summary>
public sealed class StrategySignalBroadcaster(
    IConfiguration configuration,
    RedisConnectionProvider redis,
    IHubContext<StrategyHub> hub,
    ILogger<StrategySignalBroadcaster> logger) : BackgroundService
{
    private string Stream => configuration["StrategyEvents:Stream"] ?? "strategy:v1:signal:event";
    private string Group => configuration["StrategyEvents:ApiConsumerGroup"] ?? "strategy-api-v1";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AStockObservability.ComponentStarted("strategy-signalr-broadcaster");
        var database = (await redis.GetAsync()).GetDatabase();
        await EnsureGroupAsync(database);
        var consumer = $"api-{Environment.MachineName}-{Environment.ProcessId}";
        while (!stoppingToken.IsCancellationRequested)
        {
            var entries = await database.StreamReadGroupAsync(Stream, Group, consumer, ">", 100);
            if (entries.Length == 0)
            {
                await Task.Delay(250, stoppingToken);
                continue;
            }
            foreach (var entry in entries)
            {
                try
                {
                    var payload = entry.Values.FirstOrDefault(static x => x.Name == "payload").Value.ToString();
                    using var document = JsonDocument.Parse(payload);
                    var symbol = document.RootElement.GetProperty("symbol").GetString();
                    if (!string.IsNullOrWhiteSpace(symbol))
                    {
                        await hub.Clients.Group(StrategyHub.Group(symbol))
                            .SendAsync("strategy.opportunity.changed",
                                JsonSerializer.Deserialize<JsonElement>(payload), stoppingToken);
                        AStockObservability.RecordSignalRMessage();
                    }
                    await database.StreamAcknowledgeAsync(Stream, Group, entry.Id);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "策略SignalR事件处理失败。Id={Id}", entry.Id);
                }
            }
        }
    }

    private async Task EnsureGroupAsync(IDatabase database)
    {
        try { await database.StreamCreateConsumerGroupAsync(Stream, Group, StreamPosition.Beginning, true); }
        catch (RedisServerException exception) when (
            exception.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase)) { }
    }
}
