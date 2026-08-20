using System.Threading.Channels;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Domain.Analytics;

namespace AStockMonitor.Api.Services;

/// <summary>串行处理完整采集周期，避免同一实时表上的多股并发写入形成死锁热点。</summary>
public sealed class PairTrendCollectionComputeQueue
{
    private readonly Channel<PairTrendCollectionWorkItem> _channel =
        Channel.CreateUnbounded<PairTrendCollectionWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    public ValueTask EnqueueAsync(PairTrendCollectionWorkItem item, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(item, cancellationToken);

    public IAsyncEnumerable<PairTrendCollectionWorkItem> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// API 进程内的对子内存回放器。它不是独立 Worker 服务，也不消费 Tick/Redis。
/// </summary>
public sealed class PairTrendCollectionComputeWorker(
    PairTrendCollectionComputeQueue queue,
    PairTrendCollectionSessionStore sessionStore,
    IPairTrendLiveSnapshotWriter writer,
    PairTrendNextDayValidationService nextDayValidationService,
    PairTrendQueryCache queryCache,
    ILogger<PairTrendCollectionComputeWorker> logger) : BackgroundService
{
    private readonly PairTrendV3Engine _engine = new(new PairTrendOptions());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in queue.ReadAllAsync(stoppingToken))
        {
            if (!sessionStore.TryTakeSnapshot(work.CycleId, out var snapshot) || snapshot is null)
            {
                logger.LogWarning("对子计算周期 {CycleId} 未找到内存快照。", work.CycleId);
                continue;
            }

            try
            {
                await nextDayValidationService.ProcessRealtimeSnapshotAsync(snapshot, stoppingToken);
                foreach (var symbol in snapshot.Symbols)
                {
                    if (!symbol.StrategyEligible) continue;
                    var result = _engine.Replay(symbol.Symbol, symbol.SymbolName, symbol.BarsByFrequency,
                        snapshot.TradingDate, snapshot.TradingDate);
                    await writer.WriteAsync(snapshot.TradingDate, snapshot.CycleId, result, stoppingToken);
                }
                queryCache.Invalidate();
                sessionStore.FinishProcessing(snapshot.CycleId, true);
                logger.LogInformation(
                    "对子内存周期 {CycleId} 完成：{Symbols} 只股票，{Frequencies} 个周期。",
                    snapshot.CycleId, snapshot.Symbols.Count, snapshot.Windows.Count);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                sessionStore.FinishProcessing(snapshot.CycleId, false, exception.Message);
                logger.LogError(exception, "对子内存周期 {CycleId} 写入失败；水位未推进。", snapshot.CycleId);
            }
        }
    }
}
