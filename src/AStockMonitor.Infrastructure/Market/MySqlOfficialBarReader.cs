using System.Text.Json;
using AStockMonitor.Application.Market;
using AStockMonitor.Domain.Market;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Infrastructure.Market;

/// <summary>
/// Unified read path for the four official K-line frequencies. Redis is only
/// consulted for the current unclosed official bar; historical facts always
/// come from MySQL and require <c>official_confirmed=true</c>.
/// </summary>
public sealed class MySqlOfficialBarReader(
    IMySqlConnectionFactory connectionFactory,
    RedisConnectionProvider redis) : IOfficialBarReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    public async Task<MarketBar?> GetLatestAsync(
        string symbol,
        string frequency,
        bool includeActive,
        CancellationToken cancellationToken)
    {
        var normalizedFrequency = NormalizeFrequency(frequency);
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        if (includeActive)
        {
            var active = await TryGetActiveAsync(
                normalizedSymbol, normalizedFrequency, cancellationToken);
            if (active is not null)
            {
                return active;
            }
        }

        var query = BuildQuery(normalizedFrequency, latest: true);
        await using var connection = connectionFactory.Create();
        var row = await connection.QuerySingleOrDefaultAsync<OfficialBarRow>(new CommandDefinition(
            query,
            new { Symbol = normalizedSymbol, Limit = 1 },
            cancellationToken: cancellationToken));
        return row?.ToDomain();
    }

    public async Task<IReadOnlyCollection<MarketBar>> GetBarsAsync(
        string symbol,
        string frequency,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken)
    {
        var normalizedFrequency = NormalizeFrequency(frequency);
        var query = BuildQuery(normalizedFrequency, latest: false);
        var fromChina = TimeZoneInfo.ConvertTime(from, ChinaTimeZone()).DateTime;
        var toChina = TimeZoneInfo.ConvertTime(to, ChinaTimeZone()).DateTime;
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<OfficialBarRow>(new CommandDefinition(
            query,
            new
            {
                Symbol = symbol.Trim().ToUpperInvariant(),
                From = fromChina,
                To = toChina,
                Limit = Math.Clamp(limit, 1, 10_000)
            },
            cancellationToken: cancellationToken));
        return rows.Reverse().Select(static row => row.ToDomain()).ToArray();
    }

    private async Task<MarketBar?> TryGetActiveAsync(
        string symbol,
        string frequency,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var tradingDate = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ChinaTimeZone())
                .Date.ToString("yyyy-MM-dd");
            var database = (await redis.GetAsync()).GetDatabase();
            var payload = await database.StringGetAsync(
                $"md:v2:bar:active:{tradingDate}:{frequency}:{symbol}");
            if (!payload.HasValue)
            {
                return null;
            }

            var bar = JsonSerializer.Deserialize<MarketBar>(payload.ToString(), JsonOptions);
            return bar is { IsClosed: false, OfficialConfirmed: false } ? bar : null;
        }
        catch
        {
            // Active data is an optional real-time projection. A Redis outage
            // must not hide the latest durable official bar from MySQL.
            return null;
        }
    }

    private static string BuildQuery(string frequency, bool latest)
    {
        var (table, frequencyPredicate) = frequency switch
        {
            MarketBarFrequencies.Minute5 => ("kline_bar_5m", string.Empty),
            MarketBarFrequencies.Minute30 => ("kline_bar_agg", "AND frequency='30m'"),
            MarketBarFrequencies.Minute60 => ("kline_bar_agg", "AND frequency='60m'"),
            MarketBarFrequencies.Daily => ("kline_bar_daily", string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency))
        };
        var rangePredicate = latest ? string.Empty : "AND eob BETWEEN @From AND @To";
        return $"""
            SELECT '{frequency}' Frequency, symbol Symbol, trading_date TradingDate,
                   bob Bob, eob Eob, open_price OpenPrice, high_price HighPrice,
                   low_price LowPrice, close_price ClosePrice, pre_close PreClose,
                   volume Volume, amount Amount, revision Revision, source Source,
                   official_confirmed OfficialConfirmed, row_hash RowHash,
                   source_priority SourcePriority, quality_status QualityStatus,
                   recovery_run_id RecoveryRunId,
                   COALESCE(source_updated_at, updated_at) SourceUpdatedAt
            FROM {table}
            WHERE symbol=@Symbol {frequencyPredicate} {rangePredicate}
              AND official_confirmed=TRUE AND source_priority>=300
            ORDER BY eob DESC
            LIMIT @Limit;
            """;
    }

    private static string NormalizeFrequency(string frequency)
    {
        var normalized = frequency.Trim().ToLowerInvariant();
        if (!MarketBarFrequencies.IsSupported(normalized))
        {
            throw new ArgumentException(
                "Official frequency must be one of 5m, 30m, 60m or 1d.",
                nameof(frequency));
        }
        return normalized;
    }

    private static TimeZoneInfo ChinaTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "China Standard Time" : "Asia/Shanghai");

    private sealed class OfficialBarRow
    {
        public string Frequency { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public DateTime TradingDate { get; init; }
        public DateTime Bob { get; init; }
        public DateTime Eob { get; init; }
        public decimal OpenPrice { get; init; }
        public decimal HighPrice { get; init; }
        public decimal LowPrice { get; init; }
        public decimal ClosePrice { get; init; }
        public decimal? PreClose { get; init; }
        public long Volume { get; init; }
        public decimal Amount { get; init; }
        public int Revision { get; init; }
        public string Source { get; init; } = string.Empty;
        public bool OfficialConfirmed { get; init; }
        public string RowHash { get; init; } = string.Empty;
        public int SourcePriority { get; init; }
        public string QualityStatus { get; init; } = "unchecked";
        public long? RecoveryRunId { get; init; }
        public DateTime SourceUpdatedAt { get; init; }

        public MarketBar ToDomain()
        {
            var bob = new DateTimeOffset(DateTime.SpecifyKind(Bob, DateTimeKind.Unspecified), ChinaOffset);
            var eob = new DateTimeOffset(DateTime.SpecifyKind(Eob, DateTimeKind.Unspecified), ChinaOffset);
            return new MarketBar(
                Symbol, Frequency, DateOnly.FromDateTime(TradingDate), bob, eob,
                OpenPrice, HighPrice, LowPrice, ClosePrice, PreClose, Volume, Amount,
                true, true, true, Revision, Source, OfficialConfirmed, bob, eob,
                RowHash, SourcePriority, RecoveryRunId, false, QualityStatus, null,
                new DateTimeOffset(
                    DateTime.SpecifyKind(SourceUpdatedAt, DateTimeKind.Unspecified), ChinaOffset));
        }
    }
}
