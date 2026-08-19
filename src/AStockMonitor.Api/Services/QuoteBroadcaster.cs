using AStockMonitor.Api.Hubs;
using AStockMonitor.Application.Market;
using AStockMonitor.Domain.Market;
using AStockMonitor.Infrastructure.Observability;
using Microsoft.AspNetCore.SignalR;

namespace AStockMonitor.Api.Services;

public sealed class QuoteBroadcaster(
    MarketEventBus eventBus,
    IMarketStateStore stateStore,
    IHubContext<MarketHub> hubContext,
    ILogger<QuoteBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AStockObservability.ComponentStarted("signalr-broadcaster");
        try
        {
            await using var subscription = eventBus.Subscribe("signalr-quote-broadcaster");
            await foreach (var tick in subscription.Reader.ReadAllAsync(stoppingToken))
            {
                var quote = stateStore.Get(tick.Symbol);
                if (quote is null)
                {
                    continue;
                }

                await hubContext.Clients
                    .Group(MarketSymbols.GroupName(tick.Symbol))
                    .SendAsync("quote.delta", quote, stoppingToken);
                AStockObservability.RecordSignalRMessage();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Quote broadcaster stopping");
        return base.StopAsync(cancellationToken);
    }
}
