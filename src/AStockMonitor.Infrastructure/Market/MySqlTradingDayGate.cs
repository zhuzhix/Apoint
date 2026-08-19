using AStockMonitor.Application.Market;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Infrastructure.Market;

/// <summary>
/// 只承认 authoritative_universe_sync 的同日 completed 结论；绝不从上一交易日推断。
/// </summary>
public sealed class MySqlTradingDayGate(IMySqlConnectionFactory connectionFactory) : ITradingDayGate
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private DateOnly? _cachedDate;
    private bool _cachedValue;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<bool> IsTradingDayAsync(
        DateOnly tradingDate,
        CancellationToken cancellationToken = default)
    {
        if (_cachedDate == tradingDate && DateTimeOffset.UtcNow < _expiresAt)
            return _cachedValue;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (_cachedDate == tradingDate && DateTimeOffset.UtcNow < _expiresAt)
                return _cachedValue;

            await using var connection = connectionFactory.Create();
            _cachedValue = await connection.QuerySingleAsync<bool>(new CommandDefinition(
                """
                SELECT COALESCE((
                    SELECT is_trading_day
                    FROM authoritative_universe_sync
                    WHERE trading_date=@TradingDate AND status='completed'
                    LIMIT 1),FALSE);
                """,
                new { TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue) },
                commandTimeout: 3,
                cancellationToken: cancellationToken));
            _cachedDate = tradingDate;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(5);
            return _cachedValue;
        }
        finally
        {
            _mutex.Release();
        }
    }
}
