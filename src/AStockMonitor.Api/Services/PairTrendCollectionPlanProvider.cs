using AStockMonitor.Api.Models;
using AStockMonitor.Application.Collection;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Api.Services;

/// <summary>按中国交易时段生成严格的已闭合 K 线采集计划，股票池和交易日均以 API 所连 MySQL 为准。</summary>
public sealed class PairTrendCollectionPlanProvider(
    IMySqlConnectionFactory connectionFactory,
    ICollectorOperationsRepository collectorOperations,
    IAuthoritativeUniverseRepository universeRepository,
    AuthoritativeUniverseOptions universeOptions,
    IConfiguration configuration,
    PairTrendCollectionSessionStore sessionStore)
{
    private static readonly TimeZoneInfo ChinaTimeZone = ResolveChinaTimeZone();
    private const int DefaultCompletedBarGraceSeconds = 90;
    private const int MaximumCompletedBarGraceSeconds = 600;
    private const int DefaultDailyBarGraceSeconds = 7_200;
    private const int MaximumDailyBarGraceSeconds = 43_200;

    public async Task<PairTrendCollectionPlanResponse> GetPlanAsync(
        DateOnly? requestedTradingDate,
        CancellationToken cancellationToken)
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ChinaTimeZone);
        var today = DateOnly.FromDateTime(now);
        var tradingDate = requestedTradingDate ?? today;
        var maximumBackfillDays = Math.Clamp(universeOptions.MaximumHistoricalBackfillDays, 1, 31);
        if (tradingDate > today || tradingDate < today.AddDays(-maximumBackfillDays))
        {
            return NoPlan(
                $"采集日期必须位于 {today.AddDays(-maximumBackfillDays):yyyy-MM-dd} 至 {today:yyyy-MM-dd}。",
                tradingDate);
        }
        var sync = await universeRepository.GetStatusAsync(tradingDate, cancellationToken);
        if (sync is null)
        {
            return NoPlan("API 尚未收到请求交易日的权威交易日和 A 股股票池同步。", tradingDate);
        }
        if (!sync.IsReady)
        {
            return NoPlan("请求交易日的权威股票池同步凭证与当日状态表不一致，拒绝下发计划。", tradingDate);
        }
        if (!sync.IsTradingDay)
        {
            return NoPlan("权威数据源已确认请求日期不是交易日。", tradingDate);
        }
        var minimumSymbols = Math.Clamp(universeOptions.MinimumTradingDaySymbols, 1, 20_000);
        var maximumSymbols = Math.Clamp(universeOptions.MaximumTradingDaySymbols, minimumSymbols, 20_000);
        if (sync.TotalSymbols < minimumSymbols || sync.TotalSymbols > maximumSymbols)
        {
            return NoPlan("请求交易日的权威股票池规模未通过完整性门禁。", tradingDate);
        }
        var minimumEligible = Math.Clamp(
            universeOptions.MinimumEligibleTradingDaySymbols, 1, 20_000);
        if (sync.EligibleSymbols < minimumEligible || sync.EligibleSymbols > sync.TotalSymbols)
        {
            return NoPlan("请求交易日的权威可采集股票数未通过完整性门禁。", tradingDate);
        }

        var completedBarGraceSeconds = Math.Clamp(
            configuration.GetValue<int?>("PairTrendCollection:CompletedBarGraceSeconds")
                ?? DefaultCompletedBarGraceSeconds,
            0,
            MaximumCompletedBarGraceSeconds);
        var available = PairTrendCollectionWindowPlanner.BuildAvailableWindows(
            tradingDate,
            now,
            TimeSpan.FromSeconds(completedBarGraceSeconds),
            TimeSpan.FromSeconds(Math.Clamp(
                configuration.GetValue<int?>("PairTrendCollection:DailyBarGraceSeconds")
                    ?? DefaultDailyBarGraceSeconds,
                0,
                MaximumDailyBarGraceSeconds)));
        if (available.Count == 0)
        {
            return NoPlan("当前尚无已闭合的交易时段 K 线。", tradingDate);
        }

        await using var connection = connectionFactory.Create();
        var universe = (await connection.QueryAsync<PairTrendCollectionSymbol>(new CommandDefinition(
            """
            SELECT s.symbol AS Symbol,MAX(COALESCE(i.name,s.name)) AS Name
            FROM instrument_daily_status s
            LEFT JOIN instrument i ON i.symbol=s.symbol
            WHERE s.trading_date=@TradingDate AND s.is_eligible=TRUE
              AND (s.symbol LIKE 'SHSE.%' OR s.symbol LIKE 'SZSE.%')
            GROUP BY s.symbol
            ORDER BY s.symbol;
            """,
            new { TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue) },
            cancellationToken: cancellationToken))).ToArray();
        if (universe.Length != sync.EligibleSymbols)
            return NoPlan("当日可采集股票数与权威同步凭证不一致，拒绝下发计划。", tradingDate);
        // 当前采集黑名单只描述“现在”的供应商/证券故障，不能用于篡改历史日的
        // 点时股票池。历史补算必须尝试权威状态中全部 eligible 证券并整批守恒。
        var blacklisted = tradingDate == today
            ? await collectorOperations.GetActiveBlacklistedSymbolsAsync(cancellationToken)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var symbols = universe.Where(item => !blacklisted.Contains(item.Symbol)).ToArray();
        if (symbols.Length == 0)
        {
            return NoPlan(universe.Length == 0
                ? "API 数据库尚未准备好当日可交易股票池。"
                : "当日股票池全部处于采集黑名单，等待黑名单到期后再计划。", tradingDate);
        }

        return sessionStore.BeginPlan(tradingDate, symbols, available);
    }

    private static PairTrendCollectionPlanResponse NoPlan(string reason, DateOnly tradingDate) => new(
        false, reason, null, tradingDate, null, Array.Empty<PairTrendCollectionWindow>(),
        Array.Empty<PairTrendCollectionSymbol>(), 0);

    private static TimeZoneInfo ResolveChinaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
    }
}
