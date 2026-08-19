namespace AStockMonitor.Application.Strategies;

public sealed record StrategyReplayTaskWork(
    Guid TaskId,
    string Symbol,
    DateOnly DateFrom,
    DateOnly DateTo,
    Guid SourceCommandId,
    int AttemptCount);

public interface IStrategyReplayTaskRepository
{
    Task<StrategyReplayTaskWork?> TryClaimAsync(CancellationToken cancellationToken);
    Task CompleteAsync(Guid taskId, CancellationToken cancellationToken);
    Task FailAsync(Guid taskId, string error, CancellationToken cancellationToken);
}
