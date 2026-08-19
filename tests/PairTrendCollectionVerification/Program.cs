using AStockMonitor.Api.Models;
using AStockMonitor.Api.Services;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Application.Collection;
using AStockMonitor.Domain.Analytics;

var store = new PairTrendCollectionSessionStore();
var tradingDate = new DateOnly(2026, 8, 17);
var symbols = new[] { new PairTrendCollectionSymbol("SHSE.600000", "验证证券") };
var firstWindow = new PairTrendCollectionWindow("5m",
    tradingDate.ToDateTime(new TimeOnly(9, 30)), tradingDate.ToDateTime(new TimeOnly(9, 35)));

var firstPlan = store.BeginPlan(tradingDate, symbols, [firstWindow]);
Require(firstPlan.ShouldCollect && firstPlan.Mode == "bootstrap", "首轮必须是 bootstrap。");
Require(firstPlan.CycleId is not null, "首轮必须生成 cycleId。");

store.AcceptBatch(firstPlan.CycleId!,
[
    new PairTrendCollectedBar("SHSE.600000", "5m", firstWindow.From, firstWindow.To,
        10.11m, 10.11m, 10.11m, 10.11m, 10.00m, 1000, 10110m, "verification-row-1")
]);
var work = store.Complete(firstPlan.CycleId!, new PairTrendCollectionCompleteRequest(["SHSE.600000"]));
Require(store.TryTakeSnapshot(work.CycleId, out var snapshot) && snapshot is not null,
    "完成采集后必须能取得内存快照。");

var result = new PairTrendV3Engine(new PairTrendOptions()).Replay("SHSE.600000", "验证证券",
    snapshot!.Symbols.Single().BarsByFrequency, tradingDate, tradingDate);
Require(result.Events.Count == 2, "同一根对子价位 K 线应产生顶、底两个候选事件。");

var nextTradingDate = tradingDate.AddDays(1);
var deniedCrossDate = store.BeginPlan(nextTradingDate, symbols,
[
    new PairTrendCollectionWindow("5m",
        nextTradingDate.ToDateTime(new TimeOnly(9, 30)),
        nextTradingDate.ToDateTime(new TimeOnly(9, 35)))
]);
Require(!deniedCrossDate.ShouldCollect && deniedCrossDate.TradingDate == nextTradingDate,
    "前一交易日仍在计算时，跨日计划必须明确拒绝并返回请求日期。");
Require(store.TryTakeSnapshot(work.CycleId, out var preservedSnapshot) && preservedSnapshot is not null,
    "跨日期请求不得清空正在计算的原 cycle。");

store.FinishProcessing(work.CycleId, true);
var status = store.GetStatus();
Require(status.Watermarks["5m"] == firstWindow.To, "成功后必须推进 5m 水位。");

var secondWindow = firstWindow with { To = tradingDate.ToDateTime(new TimeOnly(9, 40)) };
var secondPlan = store.BeginPlan(tradingDate, symbols, [secondWindow]);
Require(secondPlan.ShouldCollect && secondPlan.Mode == "incremental", "第二轮必须是 incremental。");
Require(secondPlan.Windows.Single().From == firstWindow.From,
    "增量轮必须向前重叠一个 5m 周期。");
store.Abort(secondPlan.CycleId!, "verification abort");
Require(store.GetStatus().Status == "failed", "显式中止不得推进水位，且应释放采集计划。");

var fullDayStore = new PairTrendCollectionSessionStore();
var fullDayOpen = tradingDate.ToDateTime(new TimeOnly(9, 30));
var fullDayClose = tradingDate.ToDateTime(new TimeOnly(15, 0));
var fullDayWindows = new[]
{
    new PairTrendCollectionWindow("5m", fullDayOpen, fullDayClose),
    new PairTrendCollectionWindow("30m", fullDayOpen, fullDayClose),
    new PairTrendCollectionWindow("60m", fullDayOpen, fullDayClose),
    new PairTrendCollectionWindow("1d", fullDayOpen, fullDayClose)
};
var fullDayPlan = fullDayStore.BeginPlan(tradingDate, symbols, fullDayWindows);
var fullDayBars = new List<PairTrendCollectedBar>();
AddBars("5m", FiveMinuteCloses(), TimeSpan.FromMinutes(5));
AddBars("30m",
[
    new(10, 0), new(10, 30), new(11, 0), new(11, 30),
    new(13, 30), new(14, 0), new(14, 30), new(15, 0)
], TimeSpan.FromMinutes(30));
AddBars("60m", [new(10, 30), new(11, 30), new(14, 0), new(15, 0)],
    TimeSpan.FromMinutes(60));
AddBars("1d", [new(15, 0)], TimeSpan.FromHours(5.5));
Require(fullDayBars.Count == 61, "单股全交易日必须精确生成 48+8+4+1=61 根 K 线。");
fullDayStore.AcceptBatch(fullDayPlan.CycleId!, fullDayBars);
var fullDayWork = fullDayStore.Complete(fullDayPlan.CycleId!,
    new PairTrendCollectionCompleteRequest(["SHSE.600000"]));
Require(fullDayStore.TryTakeSnapshot(fullDayWork.CycleId, out var fullDaySnapshot) &&
        fullDaySnapshot!.Symbols.Single().BarsByFrequency.Sum(static item => item.Value.Count) == 61,
    "精确 61 根闭合 K 线才允许全日快照进入计算队列。");

var incompleteStore = new PairTrendCollectionSessionStore();
var twoCloseWindow = new PairTrendCollectionWindow("5m", fullDayOpen,
    tradingDate.ToDateTime(new TimeOnly(9, 40)));
var incompletePlan = incompleteStore.BeginPlan(tradingDate, symbols, [twoCloseWindow]);
incompleteStore.AcceptBatch(incompletePlan.CycleId!,
[
    Bar("5m", tradingDate.ToDateTime(new TimeOnly(9, 35)), TimeSpan.FromMinutes(5), "missing-0940")
]);
ExpectInvalidOperation(() => incompleteStore.Complete(incompletePlan.CycleId!,
    new PairTrendCollectionCompleteRequest(["SHSE.600000"])));

var sparseStore = new PairTrendCollectionSessionStore();
var sparsePlan = sparseStore.BeginPlan(tradingDate, symbols, [twoCloseWindow]);
sparseStore.AcceptBatch(sparsePlan.CycleId!,
[
    Bar("5m", tradingDate.ToDateTime(new TimeOnly(9, 35)), TimeSpan.FromMinutes(5),
        "sparse-official-0935")
]);
var missing0940 = tradingDate.ToDateTime(new TimeOnly(9, 40));
ExpectInvalidOperation(() => sparseStore.Complete(sparsePlan.CycleId!,
    new PairTrendCollectionCompleteRequest(["SHSE.600000"], SparseManifest:
    [
        new PairTrendCollectionSparseManifest("SHSE.600000", "5m", [missing0940], 2)
    ])));
ExpectInvalidOperation(() => sparseStore.Complete(sparsePlan.CycleId!,
    new PairTrendCollectionCompleteRequest(["SHSE.600000"], SparseManifest:
    [
        new PairTrendCollectionSparseManifest("SHSE.600000", "5m",
            [tradingDate.ToDateTime(new TimeOnly(9, 35))], 3)
    ])));
var sparseWork = sparseStore.Complete(sparsePlan.CycleId!,
    new PairTrendCollectionCompleteRequest(["SHSE.600000"], SparseManifest:
    [
        new PairTrendCollectionSparseManifest("SHSE.600000", "5m", [missing0940], 3)
    ]));
Require(sparseStore.TryTakeSnapshot(sparseWork.CycleId, out var sparseSnapshot) &&
        sparseSnapshot!.Symbols.Single().BarsByFrequency["5m"].Count == 1,
    "三次一致的精确缺口证明只放行真实收到的 K 线，绝不能合成 09:40。" );

var idempotentStore = new PairTrendCollectionSessionStore();
var idempotentPlan = idempotentStore.BeginPlan(tradingDate, symbols, [firstWindow]);
var idempotentBar = Bar("5m", firstWindow.To, TimeSpan.FromMinutes(5), "same-official-hash");
idempotentStore.AcceptBatch(idempotentPlan.CycleId!, [idempotentBar]);
idempotentStore.AcceptBatch(idempotentPlan.CycleId!, [idempotentBar]);
ExpectInvalidOperation(() => idempotentStore.AcceptBatch(idempotentPlan.CycleId!,
    [idempotentBar with { SourceRowHash = "conflicting-official-hash" }]));
ExpectInvalidOperation(() => idempotentStore.AcceptBatch(idempotentPlan.CycleId!,
    [idempotentBar with
    {
        ClosePrice = 10.10m,
        LowPrice = 10.10m
    }]));
ExpectInvalidOperation(() => idempotentStore.Complete(idempotentPlan.CycleId!,
    new PairTrendCollectionCompleteRequest(["SHSE.600000"], SparseManifest:
    [
        new PairTrendCollectionSparseManifest("SHSE.600000", "5m", [firstWindow.To], 3)
    ])));
idempotentStore.Complete(idempotentPlan.CycleId!,
    new PairTrendCollectionCompleteRequest(["SHSE.600000"]));

var retractionStore = new PairTrendCollectionSessionStore();
var retractionFirstPlan = retractionStore.BeginPlan(tradingDate, symbols, [firstWindow]);
retractionStore.AcceptBatch(retractionFirstPlan.CycleId!,
    [Bar("5m", firstWindow.To, TimeSpan.FromMinutes(5), "official-first-cycle-0935")]);
var retractionFirstWork = retractionStore.Complete(retractionFirstPlan.CycleId!,
    new PairTrendCollectionCompleteRequest(["SHSE.600000"]));
retractionStore.FinishProcessing(retractionFirstWork.CycleId, true);
var retractionSecondPlan = retractionStore.BeginPlan(tradingDate, symbols, [twoCloseWindow]);
retractionStore.AcceptBatch(retractionSecondPlan.CycleId!,
    [Bar("5m", missing0940, TimeSpan.FromMinutes(5), "official-second-cycle-0940")]);
ExpectInvalidOperation(() => retractionStore.Complete(retractionSecondPlan.CycleId!,
    new PairTrendCollectionCompleteRequest(["SHSE.600000"], SparseManifest:
    [
        new PairTrendCollectionSparseManifest("SHSE.600000", "5m", [firstWindow.To], 2)
    ])));
Require(retractionStore.GetStatus().BarsInMemory == 2,
    "未通过三次确认的 manifest 不得删除上一 overlap cycle 的旧 K 线。" );
var retractionSecondWork = retractionStore.Complete(retractionSecondPlan.CycleId!,
    new PairTrendCollectionCompleteRequest(["SHSE.600000"], SparseManifest:
    [
        new PairTrendCollectionSparseManifest("SHSE.600000", "5m", [firstWindow.To], 3)
    ]));
Require(retractionStore.TryTakeSnapshot(retractionSecondWork.CycleId,
        out var retractionSnapshot) &&
        retractionSnapshot!.Symbols.Single().BarsByFrequency["5m"].Select(static item => item.Eob)
            .SequenceEqual([missing0940]),
    "三次确认官方撤回 overlap EOB 后，快照必须只保留真实存在的 09:40 K 线。" );

var irregularStore = new PairTrendCollectionSessionStore();
var irregularPlan = irregularStore.BeginPlan(tradingDate, symbols, [twoCloseWindow]);
ExpectInvalidOperation(() => irregularStore.AcceptBatch(irregularPlan.CycleId!,
[
    Bar("5m", tradingDate.ToDateTime(new TimeOnly(9, 37)), TimeSpan.FromMinutes(5), "irregular-eob")
]));

var universeReady = new AuthoritativeUniverseSyncStatus(
    tradingDate, "completed", true, 5_000, 4_900, 5_000, 4_900, 5_000, 4_900,
    "official-verification", new string('a', 64), DateTime.UtcNow);
Require(universeReady.IsReady, "权威股票池凭证与当日状态计数完全一致时才允许 ready。");
Require(!(universeReady with { ActualSymbols = 4_999 }).IsReady,
    "权威股票池实际行数不一致时必须拒绝 ready。未来不能以前一日数据兜底。");
Require(!(universeReady with { MatchingSymbols = 4_999 }).IsReady,
    "等量记录只要 version/source/status_quality 任一不匹配就必须拒绝 ready。");
Require(!(universeReady with { MatchingEligibleSymbols = 4_899 }).IsReady,
    "等量可采集记录的权威元数据不匹配时必须拒绝 ready。");

foreach (var stName in new[]
         { "*ST测试", "ST测试", "S*ST测试", "SST测试", "st测试", " ＊st测试 " })
    Require(AuthoritativeUniverseSyncService.NameIndicatesSt(stName),
        $"名称 {stName} 必须由 WebAPI 判定为 ST。");
Require(!AuthoritativeUniverseSyncService.NameIndicatesSt("测试股份"),
    "普通证券名称不能被误判为 ST。");

var chinaTimeZone = ResolveChinaTimeZone();
var apiToday = DateOnly.FromDateTime(
    TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, chinaTimeZone).DateTime);
var universeService = new AuthoritativeUniverseSyncService(
    new RejectUnexpectedUniverseWriteRepository(),
    new AuthoritativeUniverseOptions
    {
        MinimumTradingDaySymbols = 1,
        MinimumEligibleTradingDaySymbols = 1,
        MaximumTradingDaySymbols = 10,
        MaximumHistoricalBackfillDays = 7
    });
await ExpectArgumentExceptionAsync(() => universeService.SynchronizeAsync(
    new AuthoritativeUniverseSyncRequest(
        "verification", apiToday, true, "dongcai-gm", DateTimeOffset.UtcNow,
        [new AuthoritativeUniverseSymbolRequest(
            "SHSE.600000", "*ST验证", false, false, null, null)]),
    CancellationToken.None));

var recordingRepository = new RecordingUniverseRepository();
var historicalUniverseService = new AuthoritativeUniverseSyncService(
    recordingRepository,
    new AuthoritativeUniverseOptions
    {
        MinimumTradingDaySymbols = 1,
        MinimumEligibleTradingDaySymbols = 1,
        MaximumTradingDaySymbols = 10,
        MaximumHistoricalBackfillDays = 7
    });
var historicalDate = apiToday.AddDays(-1);
await historicalUniverseService.SynchronizeAsync(
    new AuthoritativeUniverseSyncRequest(
        "verification-backfill", historicalDate, true, "dongcai-gm-history",
        DateTimeOffset.UtcNow,
        [new AuthoritativeUniverseSymbolRequest(
            "SHSE.600000", "*ST今日但历史正常", false, false, null, null)]),
    CancellationToken.None);
Require(recordingRepository.LastSubmission?.TradingDate == historicalDate,
    "历史股票池必须以明确日期和 dongcai-gm-history 来源进入 repository。");
await ExpectArgumentExceptionAsync(() => historicalUniverseService.SynchronizeAsync(
    new AuthoritativeUniverseSyncRequest(
        "verification-backfill", historicalDate, true, "dongcai-gm",
        DateTimeOffset.UtcNow,
        [new AuthoritativeUniverseSymbolRequest(
            "SHSE.600000", "验证股份", false, false, null, null)]),
    CancellationToken.None));
await ExpectArgumentExceptionAsync(() => historicalUniverseService.SynchronizeAsync(
    new AuthoritativeUniverseSyncRequest(
        "verification-backfill", apiToday.AddDays(-8), true, "dongcai-gm-history",
        DateTimeOffset.UtcNow,
        [new AuthoritativeUniverseSymbolRequest(
            "SHSE.600000", "验证股份", false, false, null, null)]),
    CancellationToken.None));
await ExpectArgumentExceptionAsync(() => universeService.SynchronizeAsync(
    new AuthoritativeUniverseSyncRequest(
        "verification", apiToday, true, "dongcai-gm", DateTimeOffset.UtcNow,
        [new AuthoritativeUniverseSymbolRequest(
            "SHSE.600000", "验证股份", false, true, null, null)]),
    CancellationToken.None));

IReadOnlyList<PairTrendCollectionWindow> WindowsAt(DateOnly date, DateTime localNow) =>
    PairTrendCollectionWindowPlanner.BuildAvailableWindows(
        date, localNow, TimeSpan.FromSeconds(90), TimeSpan.FromHours(2));

var beforeFirstGrace = WindowsAt(tradingDate, tradingDate.ToDateTime(new TimeOnly(9, 36, 29)));
Require(beforeFirstGrace.Count == 0,
    "09:35 K 线在 09:36:30 前仍处于 90 秒发布宽限期，不得下发。" );
var atFirstGrace = WindowsAt(tradingDate, tradingDate.ToDateTime(new TimeOnly(9, 36, 30)));
Require(atFirstGrace.Single().Frequency == "5m" && atFirstGrace.Single().To == firstWindow.To,
    "09:36:30 必须恰好开放 09:35 的 5m K 线。" );
var beforeDailyGrace = WindowsAt(tradingDate, tradingDate.ToDateTime(new TimeOnly(16, 59, 59)));
Require(beforeDailyGrace.All(static item => item.Frequency != "1d"),
    "当日日 K 必须使用独立供应商发布宽限，17:00 前不得下发。" );
var atDailyGrace = WindowsAt(tradingDate, tradingDate.ToDateTime(new TimeOnly(17, 0)));
Require(atDailyGrace.Single(static item => item.Frequency == "1d").To == fullDayClose,
    "独立日 K 宽限结束后才可开放 15:00 日 K。" );
var historicalWindows = WindowsAt(tradingDate, nextTradingDate.ToDateTime(new TimeOnly(0, 1)));
Require(historicalWindows.Count == 4 &&
        historicalWindows.All(item => item.To == fullDayClose),
    "历史补算不应用实时发布宽限，必须返回完整 48/8/4/1 全日窗口。" );

Require(OperationsHealthPolicy.ResolveOverallStatus(
        "healthy", "healthy", "healthy", "failed", 4_999) == "unhealthy",
    "采集计算失败且全市场黑名单时，总状态绝不能显示healthy。" );
Require(OperationsHealthPolicy.ResolveOverallStatus(
        "healthy", "healthy", "healthy", "idle", 1) == "degraded",
    "存在活动黑名单时，总状态至少必须显示degraded。" );

var queryOptions = new PairTrendQueryOptions();
Require(PairTrendQueryPolicy.LiveStockGroupsRoute == "live/stock-groups",
    "兼容股票分组路由不得改变现有live/events语义。");
Require(PairTrendQueryPolicy.LiveStockGroupEventsRoute == "live/stock-groups/{symbol}/events",
    "兼容组内事件路由必须保留symbol路径参数。");
Require(queryOptions.HistoricalDataEnabled && queryOptions.IntradayEnabled,
    "正式历史数据和盘中查询默认必须启用。");
Require(!queryOptions.HistoricalReplayEnabled,
    "旧历史回放默认必须关闭。");
Require(queryOptions.IntradayRefreshSeconds == 30,
    "盘中查询默认刷新周期必须是30秒。");
Require(queryOptions.UseQueryProjection && queryOptions.HistoricalGroupCacheSeconds == 60 &&
        queryOptions.IntradayGroupCacheSeconds == 10,
    "正式分组查询必须默认启用强一致窄投影和有界缓存。");

var queryToday = new DateOnly(2026, 8, 18);
var defaultRange = PairTrendQueryPolicy.ResolveRange(null, null, queryToday, queryOptions);
Require(defaultRange.From == queryToday.AddDays(-59) && defaultRange.To == queryToday,
    "历史数据默认必须覆盖含首尾的最近60天。");
Require(defaultRange.ToExclusive == queryToday.AddDays(1).ToDateTime(TimeOnly.MinValue),
    "dateTo必须转换为下一自然日零点的排他上界。");
Require(PairTrendQueryPolicy.CalculateOffset(int.MaxValue, 200) == 429496729200L,
    "极大页码的Offset必须使用long计算且不得溢出为负数。");
ExpectArgumentException(() => PairTrendQueryPolicy.ResolveRange(
    queryToday, queryToday.AddDays(-1), queryToday, queryOptions));
ExpectArgumentException(() => PairTrendQueryPolicy.ResolveRange(
    queryToday.AddDays(-queryOptions.MaximumDateRangeDays), queryToday, queryToday, queryOptions));

Require(PairTrendQueryPolicy.ResolveMarketDayStatus(null) == "CALENDAR_PENDING",
    "缺少当天权威日历时不得误报非交易日。");
Require(PairTrendQueryPolicy.ResolveMarketDayStatus(
        new PairTrendMarketDayRow("running", true, DateTime.UtcNow)) == "CALENDAR_PENDING",
    "未完成的权威日历不得开放盘中数据。");
Require(PairTrendQueryPolicy.ResolveMarketDayStatus(
        new PairTrendMarketDayRow("completed", false, DateTime.UtcNow)) == "NON_TRADING_DAY",
    "正式非交易日必须与日历未同步区分。");
Require(PairTrendQueryPolicy.ResolveMarketDayStatus(
        new PairTrendMarketDayRow("completed", true, DateTime.UtcNow)) == "TRADING_DAY",
    "正式交易日必须正确开放当天查询。");

var chinaOffset = TimeSpan.FromHours(8);
Require(PairTrendQueryPolicy.ResolveSessionStatus(
        new DateTimeOffset(2026, 8, 18, 9, 0, 0, chinaOffset), "TRADING_DAY") == "PRE_OPEN",
    "开盘前会话状态错误。");
Require(PairTrendQueryPolicy.ResolveSessionStatus(
        new DateTimeOffset(2026, 8, 18, 10, 0, 0, chinaOffset), "TRADING_DAY") == "MORNING_SESSION",
    "上午交易会话状态错误。");
Require(PairTrendQueryPolicy.ResolveSessionStatus(
        new DateTimeOffset(2026, 8, 18, 12, 0, 0, chinaOffset), "TRADING_DAY") == "MIDDAY_BREAK",
    "午间休市状态错误。");
Require(PairTrendQueryPolicy.ResolveSessionStatus(
        new DateTimeOffset(2026, 8, 18, 14, 0, 0, chinaOffset), "TRADING_DAY") == "AFTERNOON_SESSION",
    "下午交易会话状态错误。");
Require(PairTrendQueryPolicy.ResolveSessionStatus(
        new DateTimeOffset(2026, 8, 18, 15, 1, 0, chinaOffset), "TRADING_DAY") == "CLOSED",
    "收盘后状态错误。");
Require(PairTrendQueryPolicy.ResolveSessionStatus(
        new DateTimeOffset(2026, 8, 17, 10, 0, 0, chinaOffset), "NON_TRADING_DAY") == "UNAVAILABLE",
    "非交易日不得伪装成盘中会话。");

var statusEnd = new DateTime(2026, 8, 18, 15, 0, 0);
Require(PairTrendQueryPolicy.ResolveStageAtEnd(
        statusEnd, statusEnd.AddHours(-3), statusEnd.AddHours(-2), statusEnd.AddHours(-1), null) ==
    "ESTABLISHED", "截止时间状态必须选择截止前最高已到达阶段。");
Require(PairTrendQueryPolicy.ResolveStageAtEnd(
        statusEnd, statusEnd.AddHours(-3), statusEnd.AddHours(-2), statusEnd.AddHours(-1),
        statusEnd.AddMinutes(-1)) == "INVALIDATED",
    "截止前失效必须覆盖此前成立阶段。");
Require(PairTrendQueryPolicy.ResolveStageAtEnd(
        statusEnd, statusEnd, null, null, null) == "DISCOVERED",
    "等于排他上界的阶段变化不得计入查询截止状态。");

var verificationRoot = FindSolutionRoot();
var queryServiceSource = File.ReadAllText(Path.Combine(
    verificationRoot, "src", "AStockMonitor.Api", "Services", "PairTrendQueryService.cs"));
Require(!queryServiceSource.Contains("ROW_NUMBER", StringComparison.OrdinalIgnoreCase),
    "股票分组不得重新引入全事件窗口排序。");
Require(!queryServiceSource.Contains("COUNT(DISTINCT e.symbol)", StringComparison.OrdinalIgnoreCase),
    "股票组总数不得再次独立扫描事件范围。");
Require(!queryServiceSource.Contains("metadata AS (", StringComparison.Ordinal) &&
    queryServiceSource.Contains("COUNT(*) OVER() TotalGroups", StringComparison.Ordinal) &&
    queryServiceSource.Contains("FROM paged", StringComparison.Ordinal),
    "正常页必须在单次消费grouped时通过窗口计数返回股票组总数。");
Require(queryServiceSource.Contains(
        "BuildConditions(query, includeKeyword: true, alias: \"latest_candidate\")",
        StringComparison.Ordinal) &&
    queryServiceSource.Contains("latest_candidate.symbol=paged.symbol", StringComparison.Ordinal) &&
    queryServiceSource.Contains("latest_candidate.root_5m_eob=paged.LatestPivotAt", StringComparison.Ordinal),
    "分页后最新事件定位必须复用完整筛选口径并限定当前股票组及其最新顶底时间。");
Require(queryServiceSource.Contains(
        "LEFT JOIN LATERAL (", StringComparison.Ordinal) &&
    queryServiceSource.Contains("FROM pair_trend_query_event e FORCE INDEX (ix_pair_trend_query_period)", StringComparison.Ordinal) &&
    queryServiceSource.Contains("FORCE INDEX (ix_pair_trend_query_symbol_period)", StringComparison.Ordinal) &&
    queryServiceSource.Contains("ORDER BY latest_candidate.event_id DESC", StringComparison.Ordinal) &&
    queryServiceSource.Contains("frequency_mask & @FrequencyMask", StringComparison.Ordinal) &&
    queryServiceSource.Contains(") latest ON TRUE", StringComparison.Ordinal),
    "正式投影必须通过覆盖日期索引、位掩码和LATERAL按事件ID稳定定位。");
Require(queryServiceSource.Contains("BuildStockGroupSqlForAudit(query)", StringComparison.Ordinal),
    "正式执行路径必须使用可审计的股票分组SQL方法。");
Require(queryServiceSource.Contains("rows.Length > 0", StringComparison.Ordinal) &&
    queryServiceSource.Contains("BuildStockGroupCountSqlForAudit(query)", StringComparison.Ordinal) &&
    queryServiceSource.Contains("GROUP BY e.symbol", StringComparison.Ordinal),
    "越界页或真空页必须在同一事务内条件执行严格过滤后的分组计数回退。");
Require(PairTrendQueryPolicy.CalculateTotalPages(4_791, 20) == 240,
    "越界页没有股票行时仍必须保留真实total和totalPages。");
Require(PairTrendQueryPolicy.CalculateTotalPages(0, 20) == 0,
    "真正无数据时total和totalPages必须为0。");

var queryCacheSource = File.ReadAllText(Path.Combine(
    verificationRoot, "src", "AStockMonitor.Api", "Services", "PairTrendQueryCache.cs"));
var computeWorkerSource = File.ReadAllText(Path.Combine(
    verificationRoot, "src", "AStockMonitor.Api", "Services", "PairTrendCollectionComputeWorker.cs"));
Require(queryCacheSource.Contains("Revision", StringComparison.Ordinal) &&
        queryCacheSource.Contains("_inflight.GetOrAdd", StringComparison.Ordinal) &&
        queryCacheSource.Contains("AbsoluteExpirationRelativeToNow", StringComparison.Ordinal),
    "股票分组缓存必须绑定数据修订、单飞并有绝对过期边界。");
Require(computeWorkerSource.IndexOf("queryCache.Invalidate();", StringComparison.Ordinal) <
        computeWorkerSource.IndexOf("sessionStore.FinishProcessing(snapshot.CycleId, true)", StringComparison.Ordinal),
    "成功周期必须先失效分组缓存再发布完成状态。");

var projectionMigration = File.ReadAllText(Path.Combine(
    verificationRoot, "database", "migrations", "030_pair_trend_query_projection.sql"));
Require(projectionMigration.Contains("CREATE TABLE IF NOT EXISTS pair_trend_query_event", StringComparison.Ordinal) &&
        projectionMigration.Contains("AFTER INSERT ON pair_trend_live_event", StringComparison.Ordinal) &&
        projectionMigration.Contains("AFTER UPDATE ON pair_trend_live_event", StringComparison.Ordinal) &&
        projectionMigration.Contains("AFTER DELETE ON pair_trend_live_event", StringComparison.Ordinal) &&
        projectionMigration.Contains("WHERE algorithm_version='pair-trend-v3' AND root_5m_eob IS NOT NULL", StringComparison.Ordinal),
    "030必须先全量回填并通过三类触发器强一致镜像正式V3事件。");

// The wave scorer must be deterministic and must not silently synthesize a
// result when a newly listed stock has too little completed daily history.
var waveScorer = new WaveBottomScorer(new WaveBottomOptions());
var waveBars = Enumerable.Range(0, 120).Select(index =>
{
    var day = new DateTime(2026, 1, 1).AddDays(index);
    var close = 20m - index * 0.07m + (index >= 112 ? (index - 111) * 0.18m : 0m);
    return new PairTrendBar(
        "SHSE.600000", "1d", day, day.AddHours(9).AddMinutes(30), day.AddHours(15),
        close + 0.10m, close + 0.35m, close - 0.30m, close, close + 0.05m,
        1_000 + index * 10, close * (1_000 + index * 10), $"wave-{index:000}");
}).ToArray();
var completeWave = waveScorer.Evaluate(waveBars);
Require(completeWave.CalculationStatus == "COMPLETED" && completeWave.DailyBarCount == 120,
    "120根已闭合日K必须完成波段评分。");
Require(completeWave.Components.Sum(static item => item.Score) == 100 &&
        completeWave.Score is >= 0 and <= 100 && completeWave.InputHash.Length == 64,
    "波段七项权重必须严格合计100分且结果可追溯。");
var insufficientWave = waveScorer.Evaluate(waveBars.Take(59).ToArray());
Require(insufficientWave.CalculationStatus == "INSUFFICIENT_DATA" &&
        insufficientWave.Signal == "NONE" && insufficientWave.Score == 0,
    "少于60根日K必须明确标记数据不足，不能形成波段信号。");

var waveMigration = File.ReadAllText(Path.Combine(
    verificationRoot, "database", "migrations", "031_wave_bottom_signal.sql"));
Require(waveMigration.Contains("wave_bottom_collection_job", StringComparison.Ordinal) &&
        waveMigration.Contains("required_daily_bars", StringComparison.Ordinal) &&
        waveMigration.Contains("wave_calculation_status", StringComparison.Ordinal),
    "031必须同时建立持久任务队列和事件级波段结果字段。");

Console.WriteLine("PairTrend collection and grouped query verification passed.");

void AddBars(string frequency, IEnumerable<TimeOnly> closes, TimeSpan duration)
{
    foreach (var close in closes)
        fullDayBars.Add(Bar(frequency, tradingDate.ToDateTime(close), duration,
            $"full-{frequency}-{close:HHmm}"));
}

PairTrendCollectedBar Bar(string frequency, DateTime eob, TimeSpan duration, string hash) =>
    new("SHSE.600000", frequency,
        frequency == "1d" ? fullDayOpen : eob - duration, eob,
        10.11m, 10.11m, 10.11m, 10.11m, 10.00m, 1000, 10110m, hash);

static IEnumerable<TimeOnly> FiveMinuteCloses()
{
    for (var value = new TimeOnly(9, 35); value <= new TimeOnly(11, 30); value = value.AddMinutes(5))
        yield return value;
    for (var value = new TimeOnly(13, 5); value <= new TimeOnly(15, 0); value = value.AddMinutes(5))
        yield return value;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task ExpectArgumentExceptionAsync(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (ArgumentException)
    {
        return;
    }
    throw new InvalidOperationException("严格权威股票池校验失败时必须整批拒绝。");
}

static void ExpectInvalidOperation(Action action)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }
    throw new InvalidOperationException("缺失或非计划 EOB 必须被严格拒绝。");
}

static void ExpectArgumentException(Action action)
{
    try
    {
        action();
    }
    catch (ArgumentException)
    {
        return;
    }
    throw new InvalidOperationException("查询日期边界必须被严格拒绝。");
}

static TimeZoneInfo ResolveChinaTimeZone()
{
    try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
}

static string FindSolutionRoot()
{
    for (var current = new DirectoryInfo(AppContext.BaseDirectory);
         current is not null;
         current = current.Parent)
    {
        if (File.Exists(Path.Combine(current.FullName, "AStockMonitor.sln")))
            return current.FullName;
    }
    throw new InvalidOperationException("无法定位AStockMonitor.sln以验证股票分组SQL契约。");
}

sealed class RejectUnexpectedUniverseWriteRepository : IAuthoritativeUniverseRepository
{
    public Task<AuthoritativeUniverseSyncResult> SynchronizeAsync(
        AuthoritativeUniverseSubmission submission,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("校验失败的股票池不得进入 repository。");

    public Task<AuthoritativeUniverseSyncStatus?> GetStatusAsync(
        DateOnly tradingDate,
        CancellationToken cancellationToken) =>
        Task.FromResult<AuthoritativeUniverseSyncStatus?>(null);
}

sealed class RecordingUniverseRepository : IAuthoritativeUniverseRepository
{
    public AuthoritativeUniverseSubmission? LastSubmission { get; private set; }

    public Task<AuthoritativeUniverseSyncResult> SynchronizeAsync(
        AuthoritativeUniverseSubmission submission,
        CancellationToken cancellationToken)
    {
        LastSubmission = submission;
        return Task.FromResult(new AuthoritativeUniverseSyncResult(
            "completed", submission.TradingDate, submission.IsTradingDay,
            submission.Symbols.Count, submission.Symbols.Count(static item => item.IsEligible),
            submission.UniverseVersion, submission.PayloadHash, DateTime.UtcNow));
    }

    public Task<AuthoritativeUniverseSyncStatus?> GetStatusAsync(
        DateOnly tradingDate,
        CancellationToken cancellationToken) =>
        Task.FromResult<AuthoritativeUniverseSyncStatus?>(null);
}
