using System.Text.Json;
using AStockMonitor.Api.Hubs;
using AStockMonitor.Application.Market;
using AStockMonitor.Infrastructure.Market;
using AStockMonitor.Infrastructure.Observability;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace AStockMonitor.Api.Services;

/// <summary>可靠消费实时对子事件并向股票订阅组推送生命周期变化。</summary>
public sealed class PairTrendEventBroadcaster(
    IConfiguration configuration,
    RedisConnectionProvider redis,
    IHubContext<MarketHub> hub,
    ILogger<PairTrendEventBroadcaster> logger) : BackgroundService
{
    private string Stream => configuration["PairTrendEvents:Stream"] ?? "pair:v3:event";
    private string Group => configuration["PairTrendEvents:ApiConsumerGroup"]
                            ?? "pair-trend-api-signalr-v3";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AStockObservability.ComponentStarted("pair-trend-signalr-v3");
        var database = (await redis.GetAsync()).GetDatabase();
        await EnsureGroupAsync(database);
        var consumer = $"api-pair-{Environment.MachineName}-{Environment.ProcessId}";
        await RecoverPendingAsync(database, consumer, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var entries = await database.StreamReadGroupAsync(Stream, Group, consumer, ">", 100);
            if (entries.Length == 0)
            {
                await Task.Delay(250, stoppingToken);
                continue;
            }
            await ProcessAsync(database, entries, stoppingToken);
        }
    }

    private async Task RecoverPendingAsync(
        IDatabase database,
        RedisValue consumer,
        CancellationToken cancellationToken)
    {
        RedisValue start = "0-0";
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await database.StreamAutoClaimAsync(
                Stream, Group, consumer, 60_000, start, 100);
            if (result.ClaimedEntries.Length == 0)
                return;
            await ProcessAsync(database, result.ClaimedEntries, cancellationToken);
            if (result.ClaimedEntries.Length < 100)
                return;
            start = result.NextStartId;
        }
    }

    private async Task ProcessAsync(
        IDatabase database,
        IReadOnlyCollection<StreamEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            try
            {
                var fields = entry.Values.ToDictionary(
                    static value => value.Name.ToString(), static value => value.Value.ToString());
                var symbol = fields.GetValueOrDefault("symbol");
                var lifecycle = fields.GetValueOrDefault("lifecycle_type")?.ToLowerInvariant();
                var payload = fields.GetValueOrDefault("payload");
                if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(payload))
                    throw new InvalidDataException("Pair event stream entry is incomplete.");
                var body = JsonSerializer.Deserialize<JsonElement>(payload);
                await hub.Clients.Group(MarketSymbols.GroupName(symbol))
                    .SendAsync($"pairTrend.{lifecycle ?? "changed"}", body, cancellationToken);
                await hub.Clients.Group(MarketSymbols.GroupName(symbol))
                    .SendAsync("pairTrend.changed", body, cancellationToken);
                AStockObservability.RecordSignalRMessage();
                await database.StreamAcknowledgeAsync(Stream, Group, entry.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception,
                    "对子SignalR事件失败，保留Pending。Id={Id}", entry.Id);
            }
        }
    }

    private async Task EnsureGroupAsync(IDatabase database)
    {
        try
        {
            await database.StreamCreateConsumerGroupAsync(
                Stream, Group, StreamPosition.Beginning, createStream: true);
        }
        catch (RedisServerException exception) when (
            exception.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
        }
    }
}
