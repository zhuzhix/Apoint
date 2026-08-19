using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AStockMonitor.Application.Strategies;
using AStockMonitor.Application.Market;
using AStockMonitor.Domain.Market;
using AStockMonitor.Domain.Strategies;
using AStockMonitor.Infrastructure.Configuration;
using AStockMonitor.Infrastructure.Market;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;
using StackExchange.Redis;

namespace AStockMonitor.Infrastructure.Strategies;

/// <summary>统一读取Redis实时投影和MySQL历史K线，为策略构建同一水位快照。</summary>
public sealed class StrategyMarketDataReader(
    MarketOptions marketOptions,
    RedisConnectionProvider redis,
    IIntradayPreviewStore previewStore,
    IMySqlConnectionFactory connectionFactory,
    MarketCollectionV4Options collectionOptions) : IStrategyMarketDataReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    public async Task<IReadOnlyList<string>> GetEligibleSymbolsAsync(
        DateOnly tradingDate, int limit, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT symbol
            FROM instrument_daily_status
            WHERE trading_date=COALESCE(
                (SELECT MAX(trading_date) FROM instrument_daily_status WHERE trading_date<=@Date),
                (SELECT MAX(trading_date) FROM instrument_daily_status))
              AND is_eligible=TRUE AND is_suspended=FALSE AND is_st=FALSE
            ORDER BY symbol
            LIMIT @Limit;
            """;
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            sql, new { Date = tradingDate.ToDateTime(TimeOnly.MinValue), Limit = Math.Clamp(limit, 1, 10_000) },
            cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<decimal?> GetMarketAverageChangePercentAsync(
        DateOnly tradingDate, DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        var symbols = await GetEligibleSymbolsAsync(tradingDate, 10_000, cancellationToken);
        try
        {
            var quotes = await LoadLatestQuotesAsync(
                symbols, tradingDate, observedAt, cancellationToken);
            var previousCloses = await LoadPreviousClosesAsync(
                symbols, tradingDate, cancellationToken);
            var changes = quotes.Values
                .Select(item => new
                {
                    Quote = item,
                    PreClose = item.PreClose ??
                        (previousCloses.TryGetValue(item.Symbol, out var prior)
                            ? prior
                            : (decimal?)null)
                })
                .Where(static item => item.PreClose > 0)
                .Select(static item =>
                    (item.Quote.Price - item.PreClose!.Value) / item.PreClose.Value * 100m)
                .ToArray();
            return changes.Length == 0 ? null : changes.Average();
        }
        catch
        {
            return null;
        }
    }

    public async Task<StrategySnapshotInput> LoadAsync(
        string symbol,
        DateOnly tradingDate,
        DateTimeOffset observedAt,
        decimal? marketAverageChangePercent,
        CancellationToken cancellationToken = default)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var quote = await LoadLatestQuoteAsync(symbol, tradingDate, observedAt, cancellationToken);
        var minute1 = (await previewStore.GetBarsAsync(
                symbol, tradingDate, observedAt, 240, cancellationToken))
            .Select(bar => new StrategyBar(
                "1m", bar.Bob, bar.Eob, bar.Open, bar.High, bar.Low, bar.Close,
                bar.Volume, bar.Amount, bar.Eob <= observedAt, bar.Revision, bar.RowHash))
            .ToList();
        var minute30 = await LoadCombined30MinuteBarsAsync(
            symbol, tradingDate, observedAt, cancellationToken);
        var daily = await LoadDailyBarsAsync(symbol, tradingDate, 180, cancellationToken);
        if (quote is not null && quote.PreClose is null && daily.LastOrDefault() is { Close: > 0 } prior)
            quote = quote with { PreClose = prior.Close };
        if (IsLiveObservation(observedAt))
        {
            await AppendActiveBarAsync(minute30, symbol, "30m", observedAt, cancellationToken);
        }

        var watermarkText = string.Join('|', new[]
        {
            quote?.EventId ?? "no-quote",
            minute1.LastOrDefault()?.RowHash ?? "no-1m",
            minute30.LastOrDefault()?.RowHash ?? "no-30m",
            daily.LastOrDefault()?.RowHash ?? "no-1d"
        });
        var watermark = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(watermarkText))).ToLowerInvariant();
        var ready = quote is not null && daily.Count > 0;
        return new StrategySnapshotInput(
            symbol, tradingDate, observedAt, quote, minute1, minute30, daily,
            marketAverageChangePercent, watermark, ready,
            ready ? null : quote is null ? "LatestQuoteMissing" : "DailyBarsMissing");
    }

    /// <summary>一次读取一批股票的30分钟和日线窗口，Redis投影使用并发管线。</summary>
    public async Task<IReadOnlyDictionary<string, StrategySnapshotInput>> LoadBatchAsync(
        IReadOnlyCollection<string> symbols,
        DateOnly tradingDate,
        DateTimeOffset observedAt,
        decimal? marketAverageChangePercent,
        CancellationToken cancellationToken = default)
    {
        var normalized = symbols
            .Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(static symbol => symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(1_000)
            .ToArray();
        if (normalized.Length == 0)
            return new Dictionary<string, StrategySnapshotInput>();

        // Tick V3 stores the latest payload in 64 shard-local Hashes. Read one
        // HMGET per shard instead of issuing one Redis command per symbol.
        var quotesTask = LoadLatestQuotesAsync(
            normalized, tradingDate, observedAt, cancellationToken);
        var previewTasks = normalized.ToDictionary(
            static symbol => symbol,
            symbol => previewStore.GetBarsAsync(
                symbol, tradingDate, observedAt, 240, cancellationToken),
            StringComparer.OrdinalIgnoreCase);
        var minute30Task = LoadBatch30MinuteBarsAsync(
            normalized, tradingDate, observedAt, cancellationToken);
        var dailyTask = LoadBatchDailyBarsAsync(
            normalized, tradingDate, 180, cancellationToken);
        await Task.WhenAll(previewTasks.Values.Cast<Task>()
            .Append(quotesTask)
            .Append(minute30Task).Append(dailyTask));
        var quotes = await quotesTask;
        var minute30BySymbol = await minute30Task;
        var dailyBySymbol = await dailyTask;
        var result = new Dictionary<string, StrategySnapshotInput>(
            normalized.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in normalized)
        {
            var quote = quotes.GetValueOrDefault(symbol);
            var minute1 = (await previewTasks[symbol])
                .Select(bar => new StrategyBar(
                    "1m", bar.Bob, bar.Eob, bar.Open, bar.High, bar.Low, bar.Close,
                    bar.Volume, bar.Amount, bar.Eob <= observedAt, bar.Revision, bar.RowHash))
                .ToList();
            var minute30 = minute30BySymbol.GetValueOrDefault(symbol)?.ToList() ?? [];
            var daily = dailyBySymbol.GetValueOrDefault(symbol)?.ToList() ?? [];
            if (quote is not null && quote.PreClose is null && daily.LastOrDefault() is { Close: > 0 } prior)
                quote = quote with { PreClose = prior.Close };
            if (IsLiveObservation(observedAt))
                await AppendActiveBarAsync(minute30, symbol, "30m", observedAt, cancellationToken);
            var watermarkText = string.Join('|',
                quote?.EventId ?? "no-quote",
                minute1.LastOrDefault()?.RowHash ?? "no-1m",
                minute30.LastOrDefault()?.RowHash ?? "no-30m",
                daily.LastOrDefault()?.RowHash ?? "no-1d");
            var watermark = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(watermarkText))).ToLowerInvariant();
            var ready = quote is not null && daily.Count > 0;
            result[symbol] = new StrategySnapshotInput(
                symbol, tradingDate, observedAt, quote, minute1, minute30, daily,
                marketAverageChangePercent, watermark, ready,
                ready ? null : quote is null ? "LatestQuoteMissing" : "DailyBarsMissing");
        }
        return result;
    }

    private async Task<IReadOnlyDictionary<string, decimal>> LoadPreviousClosesAsync(
        IReadOnlyCollection<string> symbols,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        if (symbols.Count == 0)
            return new Dictionary<string, decimal>();
        const string sql = """
            WITH ranked AS (
                SELECT symbol,close_price,
                       ROW_NUMBER() OVER (PARTITION BY symbol ORDER BY trading_date DESC) row_number
                FROM kline_bar_daily
                WHERE symbol IN @Symbols AND trading_date<@Date
                  AND official_confirmed=TRUE AND source_priority>=300
            )
            SELECT symbol Symbol,close_price Close FROM ranked WHERE row_number=1;
            """;
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<PreviousCloseRow>(new CommandDefinition(
            sql,
            new { Symbols = symbols, Date = date.ToDateTime(TimeOnly.MinValue) },
            cancellationToken: cancellationToken));
        return rows.ToDictionary(
            static row => row.Symbol,
            static row => row.Close,
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<StrategyBar>>>
        LoadBatch30MinuteBarsAsync(
            IReadOnlyCollection<string> symbols,
            DateOnly date,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken)
    {
        const string sql = """
            WITH ranked AS (
                SELECT symbol Symbol,'30m' Frequency,bob Bob,eob Eob,
                       open_price Open,high_price High,low_price Low,close_price Close,
                       CAST(volume AS SIGNED) Volume,amount Amount,TRUE IsClosed,
                       revision Revision,row_hash RowHash,
                       ROW_NUMBER() OVER (PARTITION BY symbol ORDER BY eob DESC) RowNumber
                FROM kline_bar_agg
                WHERE symbol IN @Symbols AND frequency='30m'
                  AND official_confirmed=TRUE AND source_priority>=300
                  AND (trading_date<@Date OR (trading_date=@Date AND eob<=@ObservedLocal))
            )
            SELECT Symbol,Frequency,Bob,Eob,Open,High,Low,Close,Volume,Amount,
                   IsClosed,Revision,RowHash FROM ranked
            WHERE RowNumber<=60 ORDER BY Symbol,Eob;
            """;
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<SymbolBarRow>(new CommandDefinition(
            sql,
            new
            {
                Symbols = symbols,
                Date = date.ToDateTime(TimeOnly.MinValue),
                ObservedLocal = TimeZoneInfo.ConvertTime(observedAt, ChinaTimeZone()).DateTime
            }, cancellationToken: cancellationToken));
        return rows.GroupBy(static row => row.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<StrategyBar>)group.Select(row => row.ToChinaBar()).ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<StrategyBar>>>
        LoadBatchDailyBarsAsync(
            IReadOnlyCollection<string> symbols,
            DateOnly date,
            int limit,
            CancellationToken cancellationToken)
    {
        const string sql = """
            WITH ranked AS (
                SELECT symbol Symbol,'1d' Frequency,bob Bob,eob Eob,
                       open_price Open,high_price High,low_price Low,close_price Close,
                       CAST(volume AS SIGNED) Volume,amount Amount,TRUE IsClosed,
                       revision Revision,row_hash RowHash,
                       ROW_NUMBER() OVER (PARTITION BY symbol ORDER BY trading_date DESC) RowNumber
                FROM kline_bar_daily
                WHERE symbol IN @Symbols AND trading_date<@Date
                  AND official_confirmed=TRUE AND source_priority>=300
            )
            SELECT Symbol,Frequency,Bob,Eob,Open,High,Low,Close,Volume,Amount,
                   IsClosed,Revision,RowHash FROM ranked
            WHERE RowNumber<=@Limit ORDER BY Symbol,Eob;
            """;
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<SymbolBarRow>(new CommandDefinition(
            sql, new
            {
                Symbols = symbols,
                Date = date.ToDateTime(TimeOnly.MinValue),
                Limit = Math.Clamp(limit, 1, 500)
            }, cancellationToken: cancellationToken));
        return rows.GroupBy(static row => row.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<StrategyBar>)group.Select(row => row.ToChinaBar()).ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<LatestQuote?> LoadLatestQuoteAsync(
        string symbol, DateOnly date, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        var quotes = await LoadLatestQuotesAsync(
            [symbol], date, observedAt, cancellationToken);
        return quotes.GetValueOrDefault(symbol);
    }

    /// <summary>
    /// 从 Tick V3 的分片 Hash 批量读取最新行情。实时缓存不可用时返回空集，
    /// 不回退 MySQL，也不再读取已退出生产写路径的 V2 latest key。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, LatestQuote>> LoadLatestQuotesAsync(
        IReadOnlyCollection<string> symbols,
        DateOnly date,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, LatestQuote>(StringComparer.OrdinalIgnoreCase);
        if (!IsLiveObservation(observedAt) || symbols.Count == 0)
            return result;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var database = (await redis.GetAsync()).GetDatabase();
            var groups = symbols
                .Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
                .Select(static symbol => symbol.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .GroupBy(marketOptions.GetV3TickShard)
                .ToArray();
            var tasks = groups.Select(async group =>
            {
                var groupedSymbols = group.ToArray();
                var fields = groupedSymbols
                    .Select(static symbol => (RedisValue)symbol)
                    .ToArray();
                var values = await database.HashGetAsync(
                    marketOptions.GetV3TickLatestKey(date, group.Key), fields);
                return (Symbols: groupedSymbols, Values: values);
            }).ToArray();

            foreach (var shard in await Task.WhenAll(tasks))
            {
                for (var index = 0; index < shard.Symbols.Length; index++)
                {
                    if (!shard.Values[index].HasValue)
                        continue;
                    var tick = JsonSerializer.Deserialize<TickEvent>(
                        shard.Values[index].ToString(), JsonOptions);
                    if (tick is null || tick.EventTime > observedAt ||
                        !IsFresh(tick, observedAt))
                        continue;
                    result[shard.Symbols[index]] = LatestQuote.FromTick(tick);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 实时行情缺失必须显式表现为 LatestQuoteMissing，不能用历史
            // Tick 或旧版缓存伪装当前时点数据。
        }

        return result;
    }

    private bool IsFresh(TickEvent tick, DateTimeOffset observedAt)
    {
        var maxAge = tick.CollectionMode.Equals(
            "SNAPSHOT_POLL", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, collectionOptions.Snapshot.StaleQuoteSeconds)
            : Math.Max(1, marketOptions.TickMaxAgeSeconds);
        return observedAt - tick.EventTime <= TimeSpan.FromSeconds(maxAge) &&
               tick.ReceiveTime <= observedAt.AddSeconds(30);
    }

    private async Task<List<StrategyBar>> LoadCombined30MinuteBarsAsync(
        string symbol, DateOnly date, DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT '30m' Frequency, bob Bob, eob Eob, open_price Open, high_price High,
                   low_price Low, close_price Close, volume Volume, amount Amount,
                   TRUE IsClosed, 0 Revision, row_hash RowHash
            FROM kline_bar_agg
            WHERE symbol=@Symbol AND frequency='30m'
              AND official_confirmed=TRUE AND source_priority>=300
              AND (trading_date<@Date OR (trading_date=@Date AND eob<=@ObservedLocal))
            ORDER BY eob DESC LIMIT 60;
            """;
        await using var connection = connectionFactory.Create();
        var historical = await connection.QueryAsync<BarRow>(new CommandDefinition(sql,
            new
            {
                Symbol = symbol,
                Date = date.ToDateTime(TimeOnly.MinValue),
                ObservedLocal = TimeZoneInfo.ConvertTime(observedAt, ChinaTimeZone()).DateTime
            },
            cancellationToken: cancellationToken));
        var rows = historical.Reverse().Select(static x => x.ToChinaBar()).ToList();
        return rows.OrderBy(static x => x.Eob).TakeLast(60).ToList();
    }

    private async Task<List<StrategyBar>> LoadDailyBarsAsync(
        string symbol, DateOnly date, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT '1d' Frequency, bob Bob, eob Eob, open_price Open, high_price High,
                   low_price Low, close_price Close, volume Volume, amount Amount,
                   TRUE IsClosed, 0 Revision, row_hash RowHash
            FROM kline_bar_daily
            WHERE symbol=@Symbol AND trading_date<@Date
              AND official_confirmed=TRUE AND source_priority>=300
            ORDER BY trading_date DESC LIMIT @Limit;
            """;
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<BarRow>(new CommandDefinition(sql,
            new { Symbol = symbol, Date = date.ToDateTime(TimeOnly.MinValue), Limit = limit },
            cancellationToken: cancellationToken));
        return rows.Reverse().Select(static x => x.ToChinaBar()).ToList();
    }

    private async Task AppendActiveBarAsync(
        List<StrategyBar> target, string symbol, string frequency, DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var database = (await redis.GetAsync()).GetDatabase();
            var tradingDate = ChinaMarketSession.TradingDate(observedAt);
            var value = await database.StringGetAsync(
                $"md:v2:bar:active:{tradingDate:yyyy-MM-dd}:{frequency}:{symbol}");
            if (!value.HasValue) return;
            var bar = JsonSerializer.Deserialize<MarketBar>(value.ToString(), JsonOptions);
            if (bar is null || bar.Bob > observedAt) return;
            var strategyBar = new StrategyBar(bar.Frequency, bar.Bob, bar.Eob, bar.OpenPrice,
                bar.HighPrice, bar.LowPrice, bar.ClosePrice, bar.Volume, bar.Amount,
                bar.IsClosed, bar.Revision, bar.RowHash);
            target.RemoveAll(existing => existing.Eob == strategyBar.Eob);
            target.Add(strategyBar);
            target.Sort(static (a, b) => a.Eob.CompareTo(b.Eob));
        }
        catch
        {
            // 活动Bar缺失时使用已完成K线；DataReady由快照质量统一判断。
        }
    }

    private static bool IsLiveObservation(DateTimeOffset observedAt) =>
        Math.Abs((DateTimeOffset.UtcNow - observedAt).TotalMinutes) <= 10;

    private static TimeZoneInfo ChinaTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "China Standard Time" : "Asia/Shanghai");

    private sealed class BarRow
    {
        public string Frequency { get; init; } = string.Empty;
        public DateTime Bob { get; init; }
        public DateTime Eob { get; init; }
        public decimal Open { get; init; }
        public decimal High { get; init; }
        public decimal Low { get; init; }
        public decimal Close { get; init; }
        public long Volume { get; init; }
        public decimal Amount { get; init; }
        public bool IsClosed { get; init; }
        public int Revision { get; init; }
        public string RowHash { get; init; } = string.Empty;

        public StrategyBar ToUtcBar() => new(Frequency, AsUtc(Bob), AsUtc(Eob), Open, High, Low,
            Close, Volume, Amount, IsClosed, Revision, RowHash);
        public StrategyBar ToChinaBar() => new(Frequency, new DateTimeOffset(Bob, ChinaOffset),
            new DateTimeOffset(Eob, ChinaOffset), Open, High, Low, Close, Volume, Amount,
            IsClosed, Revision, RowHash);
        private static DateTimeOffset AsUtc(DateTime value) =>
            new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private sealed class PreviousCloseRow
    {
        public string Symbol { get; init; } = string.Empty;
        public decimal Close { get; init; }
    }

    private sealed class SymbolBarRow
    {
        public string Symbol { get; init; } = string.Empty;
        public string Frequency { get; init; } = string.Empty;
        public DateTime Bob { get; init; }
        public DateTime Eob { get; init; }
        public decimal Open { get; init; }
        public decimal High { get; init; }
        public decimal Low { get; init; }
        public decimal Close { get; init; }
        public long Volume { get; init; }
        public decimal Amount { get; init; }
        public bool IsClosed { get; init; }
        public int Revision { get; init; }
        public string RowHash { get; init; } = string.Empty;

        public StrategyBar ToChinaBar() => new(
            Frequency, new DateTimeOffset(Bob, ChinaOffset), new DateTimeOffset(Eob, ChinaOffset),
            Open, High, Low, Close, Volume, Amount, IsClosed, Revision, RowHash);
    }

}
