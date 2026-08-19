using MySqlConnector;

namespace AStockMonitor.Infrastructure.Persistence;

public interface IMySqlConnectionFactory
{
    MySqlConnection Create();
}

public sealed class MySqlConnectionFactory(string connectionString) : IMySqlConnectionFactory
{
    public MySqlConnection Create() => new(connectionString);
}
