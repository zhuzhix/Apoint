using AStockMonitor.Api.Hubs;
using AStockMonitor.Application.Market;
using AStockMonitor.Contracts.Market;
using AStockMonitor.Infrastructure.Configuration;
using AStockMonitor.Infrastructure.Market;
using AStockMonitor.Infrastructure.Observability;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace AStockMonitor.Api.Services;

/// <summary>独立可靠消费官方 Bar 生命周期事件并按股票推送。</summary>
public sealed class BarEventBroadcaster(
    MarketOptions marketOptions,
    RedisConnectionProvider redis,
    IHubContext<MarketHub> hub,
    ILogger<BarEventBroadcaster> logger) : BackgroundService
{
    private const string ConsumerGroup = "market-api-signalr-v2";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AStockObservability.ComponentStarted("bar-signalr-broadcaster-v2");
        var database = (await redis.GetAsync()).GetDatabase();
        await Task.WhenAll(Enumerable.Range(
                0, Math.Clamp(marketOptions.TickStreamShardCount, 1, 256))
            .Select(shard => SuperviseShardAsync(database, shard, stoppingToken)));
    }

    private async Task SuperviseShardAsync(
        IDatabase database,
        int shard,
        CancellationToken cancellationToken)
    {
        var stream = (RedisKey)marketOptions.GetBarEventV2StreamKey(shard);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeShardAsync(database, stream, shard, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Bar SignalR分片失败，5秒后独立重启。Shard={Shard}", shard);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task ConsumeShardAsync(
        IDatabase database,
        RedisKey stream,
        int shard,
        CancellationToken cancellationToken)
    {
        await EnsureGroupAsync(database, stream);
        var consumer = $"api-bar-{Environment.MachineName}-{Environment.ProcessId}-{shard:D2}";
        await RecoverPendingAsync(database, stream, consumer, cancellationToken);
        var nextRecovery = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= nextRecovery)
            {
                await RecoverPendingAsync(database, stream, consumer, cancellationToken);
                nextRecovery = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            var entries = await database.StreamReadGroupAsync(
                stream, ConsumerGroup, consumer, ">", 200);
            if (entries.Length == 0)
            {
                await Task.Delay(250, cancellationToken);
                continue;
            }
            await ProcessEntriesAsync(database, stream, entries, cancellationToken);
        }
    }

    private async Task RecoverPendingAsync(
        IDatabase database,
        RedisKey stream,
        RedisValue consumer,
        CancellationToken cancellationToken)
    {
        RedisValue start = "0-0";
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await database.StreamAutoClaimAsync(
                stream, ConsumerGroup, consumer, 60_000, start, 200);
            if (result.ClaimedEntries.Length == 0)
                return;
            await ProcessEntriesAsync(database, stream, result.ClaimedEntries, cancellationToken);
            if (result.ClaimedEntries.Length < 200)
                return;
            start = result.NextStartId;
        }
    }

    private async Task ProcessEntriesAsync(
        IDatabase database,
        RedisKey stream,
        IReadOnlyCollection<StreamEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            try
            {
                var payload = entry.Values.FirstOrDefault(static value => value.Name == "payload").Value;
                if (!payload.HasValue)
                    throw new InvalidDataException("Bar event has no payload.");
                var barEvent = BarLifecycleEventV2Json.Deserialize(payload.ToString());
                var message = barEvent.EventType == "BarClosed" ? "bar.closed" : "bar.revised";
                await hub.Clients.Group(MarketSymbols.GroupName(barEvent.Symbol))
                    .SendAsync(message, barEvent, cancellationToken);
                await hub.Clients.Group(MarketSymbols.GroupName(barEvent.Symbol))
                    .SendAsync("bar.lifecycle.changed", barEvent, cancellationToken);
                AStockObservability.RecordSignalRMessage();
                await database.StreamAcknowledgeAsync(stream, ConsumerGroup, entry.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception,
                    "Bar SignalR事件失败，保留Pending。Stream={Stream}, Id={Id}", stream, entry.Id);
            }
        }
    }

    private static async Task EnsureGroupAsync(IDatabase database, RedisKey stream)
    {
        try
        {
            await database.StreamCreateConsumerGroupAsync(
                stream, ConsumerGroup, StreamPosition.Beginning, createStream: true);
        }
        catch (RedisServerException exception) when (
            exception.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
        }
    }
}
