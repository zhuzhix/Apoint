using AStockMonitor.Domain.Strategies;

namespace AStockMonitor.Application.Strategies;

/// <summary>所有策略必须实现的纯计算接口。</summary>
public interface IStrategyRule
{
    StrategyDescriptor Descriptor { get; }

    ValueTask<StrategyEvaluation> EvaluateAsync(
        StrategySnapshot snapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>从Redis和MySQL构建策略所需的原始行情窗口。</summary>
public interface IStrategyMarketDataReader
{
    Task<IReadOnlyList<string>> GetEligibleSymbolsAsync(
        DateOnly tradingDate,
        int limit,
        CancellationToken cancellationToken = default);

    Task<decimal?> GetMarketAverageChangePercentAsync(
        DateOnly tradingDate,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);

    Task<StrategySnapshotInput> LoadAsync(
        string symbol,
        DateOnly tradingDate,
        DateTimeOffset observedAt,
        decimal? marketAverageChangePercent,
        CancellationToken cancellationToken = default);

    /// <summary>按股票分块构建同一水位快照，避免全市场扫描产生数据库N+1。</summary>
    Task<IReadOnlyDictionary<string, StrategySnapshotInput>> LoadBatchAsync(
        IReadOnlyCollection<string> symbols,
        DateOnly tradingDate,
        DateTimeOffset observedAt,
        decimal? marketAverageChangePercent,
        CancellationToken cancellationToken = default);
}

/// <summary>策略运行、信号、机会和可靠消息的持久化边界。</summary>
public interface IStrategyRepository
{
    Task<IReadOnlySet<string>> GetEnabledStrategyCodesAsync(
        CancellationToken cancellationToken = default);

    Task<StrategyScanRun?> TryStartRunAsync(
        string runKey,
        StrategyScanProfile profile,
        string triggerType,
        DateOnly tradingDate,
        CancellationToken cancellationToken = default);

    Task PersistEvaluationsAsync(
        StrategyScanRun run,
        IReadOnlyCollection<StrategyEvaluation> evaluations,
        CancellationToken cancellationToken = default);

    Task CompleteRunAsync(
        long runId,
        int requestedSymbols,
        int completedSymbols,
        int qualifiedSignals,
        string status,
        string? error,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StrategyOutboxMessage>> ClaimOutboxAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task MarkOutboxPublishedAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default);

    Task<int> ApplyLifecycleAsync(
        StrategyScanRun run,
        DateTimeOffset now,
        TimeSpan weakenAfter,
        TimeSpan expireAfter,
        CancellationToken cancellationToken = default);
}

public sealed record StrategyOutboxMessage(long Id, string EventId, string Payload);

/// <summary>统一计算共享特征，并保证策略不产生N+1数据查询。</summary>
public interface IStrategyFeatureEngine
{
    StrategySnapshot Build(StrategySnapshotInput input);
}
