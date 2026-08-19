using AStockMonitor.Infrastructure.Configuration;
using StackExchange.Redis;

namespace AStockMonitor.Infrastructure.Market;

public sealed class RedisConnectionProvider(MarketOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private ConnectionMultiplexer? _connection;

    public async Task<IConnectionMultiplexer> GetAsync()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        await _sync.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var configuration = ConfigurationOptions.Parse(options.RedisConnection);
            configuration.AbortOnConnectFail = false;
            configuration.ConnectRetry = Math.Max(configuration.ConnectRetry, 3);
            _connection = await ConnectionMultiplexer.ConnectAsync(configuration);
            return _connection;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _sync.Dispose();
    }
}
