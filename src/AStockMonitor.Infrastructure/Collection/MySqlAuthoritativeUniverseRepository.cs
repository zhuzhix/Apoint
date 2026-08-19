using System.Data;
using System.Text;
using System.Text.Json;
using AStockMonitor.Application.Collection;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Infrastructure.Collection;

public sealed class MySqlAuthoritativeUniverseRepository(
    IMySqlConnectionFactory connectionFactory) : IAuthoritativeUniverseRepository
{
    private const int InsertBatchSize = 500;
    private const string StatusSql = """
        SELECT s.trading_date AS TradingDate,s.status AS Status,
            s.is_trading_day AS IsTradingDay,
            s.total_symbol_count AS TotalSymbols,
            s.eligible_symbol_count AS EligibleSymbols,
            COUNT(d.symbol) AS ActualSymbols,
            COALESCE(SUM(d.is_eligible=TRUE),0) AS ActualEligibleSymbols,
            COALESCE(SUM(CASE
                WHEN BINARY d.universe_version=BINARY s.universe_version
                 AND BINARY d.source=BINARY s.source
                 AND BINARY d.status_quality=BINARY 'authoritative_daily'
                THEN 1 ELSE 0 END),0) AS MatchingSymbols,
            COALESCE(SUM(CASE
                WHEN BINARY d.universe_version=BINARY s.universe_version
                 AND BINARY d.source=BINARY s.source
                 AND BINARY d.status_quality=BINARY 'authoritative_daily'
                 AND d.is_eligible=TRUE
                THEN 1 ELSE 0 END),0) AS MatchingEligibleSymbols,
            s.universe_version AS UniverseVersion,s.payload_hash AS PayloadHash,
            s.synced_at AS SyncedAt
        FROM authoritative_universe_sync s
        LEFT JOIN instrument_daily_status d ON d.trading_date=s.trading_date
        WHERE s.trading_date=@TradingDate
        GROUP BY s.trading_date,s.status,s.is_trading_day,s.total_symbol_count,
            s.eligible_symbol_count,s.source,s.universe_version,s.payload_hash,s.synced_at;
        """;

    public async Task<AuthoritativeUniverseSyncResult> SynchronizeAsync(
        AuthoritativeUniverseSubmission submission,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var lockName = $"pair-universe-{submission.TradingDate:yyyyMMdd}";
        var lockAcquired = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT GET_LOCK(@LockName,15);", new { LockName = lockName },
            cancellationToken: cancellationToken));
        if (lockAcquired != 1)
            throw new TimeoutException("无法取得当日股票池同步锁，请稍后重试。");

        try
        {
            var existing = await ReadStatusAsync(
                connection, submission.TradingDate, cancellationToken);
            if (existing is { IsReady: true } &&
                existing.IsTradingDay == submission.IsTradingDay &&
                string.Equals(existing.PayloadHash, submission.PayloadHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new AuthoritativeUniverseSyncResult(
                    existing.Status, existing.TradingDate, existing.IsTradingDay,
                    existing.TotalSymbols, existing.EligibleSymbols,
                    existing.UniverseVersion, existing.PayloadHash, existing.SyncedAt);
            }

            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                if (submission.Symbols.Count > 0)
                    await UpsertInstrumentsAsync(connection, transaction, submission, cancellationToken);

                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM instrument_daily_status WHERE trading_date=@TradingDate;",
                    new { TradingDate = submission.TradingDate.ToDateTime(TimeOnly.MinValue) },
                    transaction, cancellationToken: cancellationToken));

                if (submission.Symbols.Count > 0)
                    await InsertDailyStatusesAsync(connection, transaction, submission, cancellationToken);

                var eligibleCount = submission.Symbols.Count(static item => item.IsEligible);
                var syncedAt = await connection.QuerySingleAsync<DateTime>(new CommandDefinition(
                    "SELECT UTC_TIMESTAMP(6);", transaction: transaction,
                    cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO authoritative_universe_sync(
                        trading_date,status,is_trading_day,collector_id,source,
                        source_updated_at,total_symbol_count,eligible_symbol_count,
                        universe_version,payload_hash,synced_at)
                    VALUES(@TradingDate,'completed',@IsTradingDay,@CollectorId,@Source,
                        @SourceUpdatedAt,@TotalSymbols,@EligibleSymbols,
                        @UniverseVersion,@PayloadHash,@SyncedAt)
                    ON DUPLICATE KEY UPDATE
                        status='completed',is_trading_day=VALUES(is_trading_day),
                        collector_id=VALUES(collector_id),source=VALUES(source),
                        source_updated_at=VALUES(source_updated_at),
                        total_symbol_count=VALUES(total_symbol_count),
                        eligible_symbol_count=VALUES(eligible_symbol_count),
                        universe_version=VALUES(universe_version),
                        payload_hash=VALUES(payload_hash),synced_at=VALUES(synced_at);
                    """,
                    new
                    {
                        TradingDate = submission.TradingDate.ToDateTime(TimeOnly.MinValue),
                        submission.IsTradingDay,
                        submission.CollectorId,
                        submission.Source,
                        SourceUpdatedAt = submission.SourceUpdatedAtUtc,
                        TotalSymbols = submission.Symbols.Count,
                        EligibleSymbols = eligibleCount,
                        submission.UniverseVersion,
                        submission.PayloadHash,
                        SyncedAt = syncedAt
                    }, transaction, cancellationToken: cancellationToken));

                await transaction.CommitAsync(cancellationToken);
                return new AuthoritativeUniverseSyncResult(
                    "completed", submission.TradingDate, submission.IsTradingDay,
                    submission.Symbols.Count, eligibleCount, submission.UniverseVersion,
                    submission.PayloadHash, DateTime.SpecifyKind(syncedAt, DateTimeKind.Utc));
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        finally
        {
            await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT RELEASE_LOCK(@LockName);", new { LockName = lockName },
                cancellationToken: CancellationToken.None));
        }
    }

    public async Task<AuthoritativeUniverseSyncStatus?> GetStatusAsync(
        DateOnly tradingDate,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        return await ReadStatusAsync(connection, tradingDate, cancellationToken);
    }

    private static async Task<AuthoritativeUniverseSyncStatus?> ReadStatusAsync(
        MySqlConnector.MySqlConnection connection,
        DateOnly tradingDate,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<StatusRow>(new CommandDefinition(
            StatusSql, new { TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue) },
            cancellationToken: cancellationToken));
        return row is null ? null : new AuthoritativeUniverseSyncStatus(
            DateOnly.FromDateTime(row.TradingDate), row.Status, row.IsTradingDay,
            row.TotalSymbols, row.EligibleSymbols, row.ActualSymbols,
            row.ActualEligibleSymbols, row.MatchingSymbols, row.MatchingEligibleSymbols,
            row.UniverseVersion, row.PayloadHash,
            DateTime.SpecifyKind(row.SyncedAt, DateTimeKind.Utc));
    }

    private static async Task UpsertInstrumentsAsync(
        MySqlConnector.MySqlConnection connection,
        MySqlConnector.MySqlTransaction transaction,
        AuthoritativeUniverseSubmission submission,
        CancellationToken cancellationToken)
    {
        foreach (var batch in submission.Symbols.Chunk(InsertBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO instrument(symbol,exchange,name,security_type,list_date,delist_date,status)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0) sql.Append(',');
                sql.Append($"(@Symbol{index},@Exchange{index},@Name{index},'stock',@ListDate{index},@DelistDate{index},'active')");
                AddInstrumentParameters(parameters, batch[index], index);
            }
            sql.Append("""
                ON DUPLICATE KEY UPDATE exchange=VALUES(exchange),name=VALUES(name),
                    security_type='stock',list_date=COALESCE(VALUES(list_date),list_date),
                    delist_date=COALESCE(VALUES(delist_date),delist_date),status='active';
                """);
            await connection.ExecuteAsync(new CommandDefinition(
                sql.ToString(), parameters, transaction, cancellationToken: cancellationToken));
        }
    }

    private static async Task InsertDailyStatusesAsync(
        MySqlConnector.MySqlConnection connection,
        MySqlConnector.MySqlTransaction transaction,
        AuthoritativeUniverseSubmission submission,
        CancellationToken cancellationToken)
    {
        var rawAttributes = JsonSerializer.Serialize(new
        {
            _universe_adapter = "collector-authoritative-daily",
            sourceUpdatedAt = submission.SourceUpdatedAtUtc
        });
        foreach (var batch in submission.Symbols.Chunk(InsertBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO instrument_daily_status(
                    symbol,trading_date,exchange,name,is_st,is_suspended,is_a_share,
                    is_eligible,status_source,status_quality,exclusion_reason,
                    adjust_factor,source,universe_version,raw_attributes)
                VALUES
                """);
            var parameters = new DynamicParameters();
            parameters.Add("TradingDate", submission.TradingDate.ToDateTime(TimeOnly.MinValue));
            parameters.Add("Source", submission.Source);
            parameters.Add("UniverseVersion", submission.UniverseVersion);
            parameters.Add("RawAttributes", rawAttributes);
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0) sql.Append(',');
                sql.Append($"(@Symbol{index},@TradingDate,@Exchange{index},@Name{index}," +
                           $"@IsSt{index},@IsSuspended{index},TRUE,@IsEligible{index}," +
                           $"@Source,'authoritative_daily',@ExclusionReason{index},NULL," +
                           $"@Source,@UniverseVersion,@RawAttributes)");
                var item = batch[index];
                parameters.Add($"Symbol{index}", item.Symbol);
                parameters.Add($"Exchange{index}", item.Exchange);
                parameters.Add($"Name{index}", item.Name);
                parameters.Add($"IsSt{index}", item.IsSt);
                parameters.Add($"IsSuspended{index}", item.IsSuspended);
                parameters.Add($"IsEligible{index}", item.IsEligible);
                parameters.Add($"ExclusionReason{index}", item.IsEligible
                    ? null : item.IsSt ? "ST" : "SUSPENDED");
            }
            sql.Append(';');
            await connection.ExecuteAsync(new CommandDefinition(
                sql.ToString(), parameters, transaction, cancellationToken: cancellationToken));
        }
    }

    private static void AddInstrumentParameters(
        DynamicParameters parameters,
        AuthoritativeUniverseSymbol item,
        int index)
    {
        parameters.Add($"Symbol{index}", item.Symbol);
        parameters.Add($"Exchange{index}", item.Exchange);
        parameters.Add($"Name{index}", item.Name);
        parameters.Add($"ListDate{index}", item.ListDate?.ToDateTime(TimeOnly.MinValue));
        parameters.Add($"DelistDate{index}", item.DelistDate?.ToDateTime(TimeOnly.MinValue));
    }

    private sealed class StatusRow
    {
        public DateTime TradingDate { get; init; }
        public string Status { get; init; } = "";
        public bool IsTradingDay { get; init; }
        public int TotalSymbols { get; init; }
        public int EligibleSymbols { get; init; }
        public int ActualSymbols { get; init; }
        public int ActualEligibleSymbols { get; init; }
        public int MatchingSymbols { get; init; }
        public int MatchingEligibleSymbols { get; init; }
        public string UniverseVersion { get; init; } = "";
        public string PayloadHash { get; init; } = "";
        public DateTime SyncedAt { get; init; }
    }
}
