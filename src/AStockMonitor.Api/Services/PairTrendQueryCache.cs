using System.Collections.Concurrent;
using AStockMonitor.Api.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AStockMonitor.Api.Services;

/// <summary>
/// Revision-keyed, bounded single-flight cache for the expensive stock-group query.
/// A completed collection cycle increments the revision, so stale entries can never be read.
/// </summary>
public sealed class PairTrendQueryCache(IMemoryCache memoryCache)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<PairTrendStockGroupPage>>> _inflight =
        new(StringComparer.Ordinal);
    private long _revision;

    public long Revision => Volatile.Read(ref _revision);

    public void Invalidate() => Interlocked.Increment(ref _revision);

    public async Task<PairTrendStockGroupPage> GetOrCreateAsync(
        string logicalKey,
        TimeSpan lifetime,
        Func<CancellationToken, Task<PairTrendStockGroupPage>> factory,
        CancellationToken cancellationToken)
    {
        var key = $"pair-groups:{Revision}:{logicalKey}";
        if (memoryCache.TryGetValue<PairTrendStockGroupPage>(key, out var cached) && cached is not null)
            return cached;

        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<PairTrendStockGroupPage>>(
            async () =>
            {
                var result = await factory(CancellationToken.None);
                memoryCache.Set(key, result, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = lifetime,
                    Size = 1
                });
                return result;
            }, LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        _ = task.ContinueWith(
            completedTask => _inflight.TryRemove(key, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken);
    }
}
