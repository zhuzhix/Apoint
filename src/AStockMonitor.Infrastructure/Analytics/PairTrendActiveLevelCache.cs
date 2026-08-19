using System.Collections.Concurrent;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Domain.Analytics;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Infrastructure.Analytics;

/// <summary>
/// 活动对子价位的进程内只读快照。全量只在进程启动时加载，之后按受影响股票刷新；
/// 使高频 Tick 的绝大多数路径只做内存比较，不产生 MySQL 查询。
/// </summary>
public sealed class PairTrendActiveLevelCache(IMySqlConnectionFactory connectionFactory)
    : IPairTrendActiveLevelCache
{
    private readonly ConcurrentDictionary<string, PairTrendActiveLevel[]> _levels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private volatile bool _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;
        await _initialization.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;
            await using var connection = connectionFactory.Create();
            var rows = (await connection.QueryAsync<PairTrendActiveLevel>(new CommandDefinition(
                SelectSql + " ORDER BY symbol,id;",
                new { AlgorithmVersion = PairTrendOptions.CurrentAlgorithmVersion },
                cancellationToken: cancellationToken))).ToArray();
            foreach (var group in rows.GroupBy(static item => item.Symbol, StringComparer.OrdinalIgnoreCase))
                _levels[group.Key] = group.ToArray();
            _initialized = true;
        }
        finally
        {
            _initialization.Release();
        }
    }

    public async Task ReloadSymbolAsync(string symbol, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        var rows = (await connection.QueryAsync<PairTrendActiveLevel>(new CommandDefinition(
            SelectSql + " AND symbol=@Symbol ORDER BY id;",
            new
            {
                Symbol = symbol.Trim().ToUpperInvariant(),
                AlgorithmVersion = PairTrendOptions.CurrentAlgorithmVersion
            }, cancellationToken: cancellationToken))).ToArray();
        if (rows.Length == 0)
            _levels.TryRemove(symbol, out _);
        else
            _levels[symbol] = rows;
    }

    public IReadOnlyList<PairTrendActiveLevel> Get(string symbol) =>
        _levels.TryGetValue(symbol, out var rows) ? rows : [];

    private const string SelectSql = """
        SELECT id EventId,event_key EventKey,symbol Symbol,symbol_name SymbolName,
               pivot_type PivotType,stage Stage,latest_pair_price PairPrice,
               price_ticks PriceTicks,latest_pair_code PairCode,
               latest_pair_kind PairKind,generation Generation,event_revision EventRevision
        FROM pair_trend_live_event
        WHERE is_active=TRUE AND algorithm_version=@AlgorithmVersion
        """;
}
