using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Domain.Analytics;

const string dailyPairAlgorithm = "pair-trend-daily-v1";
var arguments = ParseArguments(args);
var inputDirectory = RequireDirectory(arguments, "input");
var outputDirectory = Path.GetFullPath(arguments.GetValueOrDefault(
    "output", Path.Combine(inputDirectory, "results")));
Directory.CreateDirectory(outputDirectory);

var manifestPath = Path.Combine(inputDirectory, "snapshot-manifest.json");
var manifest = JsonSerializer.Deserialize<SnapshotManifest>(
    File.ReadAllText(manifestPath), JsonOptions())
    ?? throw new InvalidDataException("离线快照清单为空。");
ValidateManifest(manifest);
var targetFrom = DateOnly.Parse(manifest.TargetFrom, CultureInfo.InvariantCulture);
var targetTo = DateOnly.Parse(manifest.TargetTo, CultureInfo.InvariantCulture);
var eligibilityPath = Path.Combine(inputDirectory, "eligibility.tsv.gz");
if (!string.Equals(Sha256(eligibilityPath), manifest.EligibilitySha256,
        StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("历史股票资格快照哈希不一致。");
var eligibility = LoadEligibility(eligibilityPath);
var scorer = new WaveBottomScorer(new WaveBottomOptions());
var pairEvents = new List<DailyPairEvent>();
var bottomResults = new List<BottomResult>();
long dailyBarsRead = 0;
long eligibleTargetBarsObserved = 0;

foreach (var entry in manifest.CompletedBatches.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
{
    var path = Path.Combine(inputDirectory, "batches", $"{entry.Key}.jsonl.gz");
    if (!File.Exists(path)) throw new FileNotFoundException($"缺少离线批次 {entry.Key}", path);
    if (!string.Equals(Sha256(path), entry.Value.Sha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"离线批次哈希不一致: {entry.Key}");
    var barsBySymbol = ReadBatch(path);
    dailyBarsRead += barsBySymbol.Values.Sum(static values => values.Count);
    foreach (var symbolPair in barsBySymbol.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
    {
        var symbol = symbolPair.Key;
        var bars = symbolPair.Value
            .OrderBy(static value => value.TradingDate)
            .GroupBy(static value => value.TradingDate)
            .Select(group => group.Count() == 1
                ? group.Single()
                : throw new InvalidDataException($"日K重复: {symbol}/{group.Key}"))
            .ToArray();
        if (!eligibility.TryGetValue(symbol, out var eligibleDates))
            continue;
        eligibleTargetBarsObserved += bars.LongCount(bar =>
            bar.TradingDate >= targetFrom && bar.TradingDate <= targetTo &&
            eligibleDates.Contains(bar.TradingDate));
        var events = BuildEvents(symbol, bars, eligibleDates, targetFrom, targetTo);
        pairEvents.AddRange(events);
        foreach (var pairEvent in events.Where(static value => value.PivotType == "BOTTOM"))
        {
            // 日K对子信号在15:00收盘后成立，所以当前这根已完成日K可以进入评分。
            // 回测从下一根可交易日的开盘价开始，避免使用信号当日不可成交价格。
            var scoreInput = bars
                .Where(bar => bar.TradingDate <= pairEvent.SignalDate)
                .TakeLast(120)
                .Select(static bar => bar.ToPairTrendBar())
                .ToArray();
            var evaluation = scorer.Evaluate(scoreInput);
            var future = bars
                .Where(bar => bar.TradingDate > pairEvent.SignalDate)
                .Take(20)
                .ToArray();
            bottomResults.Add(BottomResult.Create(pairEvent, evaluation, future));
        }
    }
    Console.WriteLine(
        $"processed {entry.Key}: symbols={barsBySymbol.Count}, dailyBars={dailyBarsRead}, " +
        $"pairEvents={pairEvents.Count}, bottoms={bottomResults.Count}");
}

var eligibleTargetBarsExpected = eligibility.Values.Sum(static dates => (long)dates.Count);
if (eligibleTargetBarsObserved != eligibleTargetBarsExpected)
    throw new InvalidDataException(
        $"历史可用股票日K不守恒: {eligibleTargetBarsObserved}/{eligibleTargetBarsExpected}");
if (pairEvents.Count == 0 || bottomResults.Count == 0)
    throw new InvalidDataException("日K对子事件或底部结果为空，拒绝生成空回测报告。");

var independent = BuildIndependentSample(bottomResults);
var eligibleIndependent = independent
    .Where(static value => value.CalculationStatus == "COMPLETED" && value.Has20Days)
    .ToArray();
var cohorts = new[]
{
    Summarize("ALL_DAILY_PAIR_BOTTOM", eligibleIndependent),
    Summarize("NONE", eligibleIndependent.Where(static value => value.Signal == "NONE").ToArray()),
    Summarize("CANDIDATE", eligibleIndependent.Where(static value => value.Signal == "CANDIDATE").ToArray()),
    Summarize("STRONG", eligibleIndependent.Where(static value => value.Signal == "STRONG").ToArray()),
    Summarize("CANDIDATE_OR_STRONG", eligibleIndependent.Where(
        static value => value.Signal is "CANDIDATE" or "STRONG").ToArray())
};
var summary = new BacktestSummary(
    dailyPairAlgorithm,
    WaveBottomOptions.CurrentAlgorithmVersion,
    targetFrom,
    targetTo,
    manifest.Symbols.Count,
    dailyBarsRead,
    eligibleTargetBarsObserved,
    pairEvents.Count,
    pairEvents.Count(static value => value.PivotType == "TOP"),
    pairEvents.Count(static value => value.PivotType == "BOTTOM"),
    bottomResults.Count,
    independent.Count,
    eligibleIndependent.Length,
    eligibleIndependent.Count(static value => value.TrendGatePassed),
    eligibleIndependent.Count(static value => value.Score >= 70),
    eligibleIndependent.Length == 0 ? 0 : eligibleIndependent.Max(static value => value.Score),
    Sha256(manifestPath),
    BuildComponentSummary(eligibleIndependent),
    cohorts);

await WriteEventsAsync(Path.Combine(outputDirectory, "daily-pair-events.csv"), pairEvents);
await WriteResultsAsync(Path.Combine(outputDirectory, "wave-bottom-results.csv"), bottomResults);
await File.WriteAllTextAsync(
    Path.Combine(outputDirectory, "summary.json"),
    JsonSerializer.Serialize(summary, JsonOptions(true)));
await File.WriteAllTextAsync(Path.Combine(outputDirectory, "report.md"), BuildReport(summary));
Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions(true)));

static IReadOnlyDictionary<string, string> ParseArguments(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("参数必须使用 --name value 格式。");
        result[values[index][2..]] = values[index + 1];
    }
    return result;
}

static string RequireDirectory(IReadOnlyDictionary<string, string> values, string name)
{
    if (!values.TryGetValue(name, out var value) || !Directory.Exists(value))
        throw new DirectoryNotFoundException($"缺少 --{name} 离线输入目录。");
    return Path.GetFullPath(value);
}

static JsonSerializerOptions JsonOptions(bool indented = false) => new()
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = indented
};

static void ValidateManifest(SnapshotManifest value)
{
    if (value.SnapshotVersion != "offline-gm-v1" || value.CalculationMode != "daily-pair-v1" ||
        !value.DailyOnly || value.Stage != "downloaded")
        throw new InvalidDataException("快照不是完整的daily-pair-v1离线数据。");
    if (value.Isolation.WebApiClient || value.Isolation.MySqlClient ||
        value.Isolation.RedisClient || value.Isolation.Migration)
        throw new InvalidDataException("快照隔离声明失败，拒绝执行。");
    if (value.CompletedBatches.Count != value.RequestedBatchCount)
        throw new InvalidDataException(
            $"离线批次不完整: {value.CompletedBatches.Count}/{value.RequestedBatchCount}");
    if (value.CompletedBatches.Values.Sum(static batch => batch.Symbols) != value.Symbols.Count)
        throw new InvalidDataException("离线批次证券数与全区间并集不守恒。");
}

static Dictionary<string, HashSet<DateOnly>> LoadEligibility(string path)
{
    var values = new Dictionary<string, HashSet<DateOnly>>(StringComparer.OrdinalIgnoreCase);
    using var file = File.OpenRead(path);
    using var gzip = new GZipStream(file, CompressionMode.Decompress);
    using var reader = new StreamReader(gzip, Encoding.UTF8);
    while (reader.ReadLine() is { } line)
    {
        var fields = line.Split('\t', 3);
        if (fields.Length != 3) throw new InvalidDataException("eligibility列数错误。");
        if (!values.TryGetValue(fields[0], out var dates))
            values[fields[0]] = dates = [];
        if (!dates.Add(DateOnly.Parse(fields[1], CultureInfo.InvariantCulture)))
            throw new InvalidDataException($"eligibility重复: {fields[0]}/{fields[1]}");
    }
    return values;
}

static Dictionary<string, List<DailyBar>> ReadBatch(string path)
{
    var result = new Dictionary<string, List<DailyBar>>(StringComparer.OrdinalIgnoreCase);
    using var file = File.OpenRead(path);
    using var gzip = new GZipStream(file, CompressionMode.Decompress);
    using var reader = new StreamReader(gzip, Encoding.UTF8);
    while (reader.ReadLine() is { } line)
    {
        var row = JsonSerializer.Deserialize<DailyBarRow>(line, JsonOptions())
            ?? throw new InvalidDataException("日K JSON行为空。");
        if (!string.Equals(row.Frequency, "1d", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"daily-only批次包含非日K: {row.Frequency}");
        var bar = row.ToDailyBar();
        if (!result.TryGetValue(bar.Symbol, out var values))
            result[bar.Symbol] = values = [];
        values.Add(bar);
    }
    return result;
}

static List<DailyPairEvent> BuildEvents(
    string symbol,
    IReadOnlyCollection<DailyBar> bars,
    IReadOnlySet<DateOnly> eligibleDates,
    DateOnly dateFrom,
    DateOnly dateTo)
{
    var result = new List<DailyPairEvent>();
    var activeTop = new Dictionary<long, DailyPairEvent>();
    var activeBottom = new Dictionary<long, DailyPairEvent>();
    var generations = new Dictionary<(string Pivot, long Ticks), int>();
    foreach (var bar in bars.Where(value => value.TradingDate >= dateFrom &&
                 value.TradingDate <= dateTo && eligibleDates.Contains(value.TradingDate)))
    {
        Invalidate(activeTop, bar.HighPrice, bar.TradingDate, higherBreak: true);
        Invalidate(activeBottom, bar.LowPrice, bar.TradingDate, higherBreak: false);
        Discover("TOP", bar.HighPrice, bar, activeTop);
        Discover("BOTTOM", bar.LowPrice, bar, activeBottom);
    }
    return result;

    void Invalidate(
        IDictionary<long, DailyPairEvent> active,
        decimal currentPrice,
        DateOnly tradingDate,
        bool higherBreak)
    {
        foreach (var pair in active.ToArray())
        {
            if (higherBreak ? currentPrice <= pair.Value.PairPrice : currentPrice >= pair.Value.PairPrice)
                continue;
            pair.Value.IsActive = false;
            pair.Value.InvalidatedAt = tradingDate;
            pair.Value.InvalidationReason = higherBreak ? "HIGHER_DAILY_HIGH" : "LOWER_DAILY_LOW";
            active.Remove(pair.Key);
        }
    }

    void Discover(
        string pivot,
        decimal price,
        DailyBar bar,
        IDictionary<long, DailyPairEvent> active)
    {
        var match = PairPriceMatcher.Match(price);
        if (match is null || active.ContainsKey(match.PriceTicks)) return;
        var generationKey = (pivot, match.PriceTicks);
        var generation = generations.GetValueOrDefault(generationKey) + 1;
        generations[generationKey] = generation;
        var identity = string.Join('|', symbol, pivot, match.PriceTicks,
            bar.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            generation, dailyPairAlgorithm);
        var item = new DailyPairEvent(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
            symbol, pivot, bar.TradingDate, bar.TradingDate.ToDateTime(new TimeOnly(15, 0)),
            match.Price, match.PairCode, match.Kind.ToString(), generation,
            true, null, null, bar.SourceRowHash);
        active.Add(match.PriceTicks, item);
        result.Add(item);
    }
}

static List<BottomResult> BuildIndependentSample(IEnumerable<BottomResult> values)
{
    var output = new List<BottomResult>();
    foreach (var group in values.GroupBy(static value => value.Symbol, StringComparer.OrdinalIgnoreCase))
    {
        DateOnly? blockedThrough = null;
        foreach (var value in group.OrderBy(static item => item.SignalDate).ThenBy(static item => item.EventKey))
        {
            if (blockedThrough is not null && value.SignalDate <= blockedThrough) continue;
            output.Add(value);
            if (value.OutcomeThrough is not null) blockedThrough = value.OutcomeThrough;
        }
    }
    return output.OrderBy(static value => value.SignalDate).ThenBy(static value => value.EventKey).ToList();
}

static CohortSummary Summarize(string name, IReadOnlyCollection<BottomResult> values)
{
    if (values.Count == 0)
        return new CohortSummary(name, 0, null, null, null, null, null, null, null, null);
    var return5 = values.Select(static value => value.Return5!.Value).ToArray();
    var return10 = values.Select(static value => value.Return10!.Value).ToArray();
    var return20 = values.Select(static value => value.Return20!.Value).ToArray();
    return new CohortSummary(
        name,
        values.Count,
        Round(Median(return5)),
        Round(Median(return10)),
        Round(Median(return20)),
        Round(return20.Average()),
        Round(values.Count(static value => value.Return20 > 0m) * 100m / values.Count),
        Round(Median(values.Select(static value => value.Mfe20!.Value).ToArray())),
        Round(Median(values.Select(static value => value.Mae20!.Value).ToArray())),
        Round(values.Count(static value => value.FirstBarrier == "PLUS5") * 100m / values.Count));
}

static IReadOnlyCollection<ComponentSummary> BuildComponentSummary(
    IReadOnlyCollection<BottomResult> values)
{
    if (values.Count == 0) return [];
    return values
        .SelectMany(static value => value.MatchedComponents.Split(
            '|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .GroupBy(static value => value, StringComparer.Ordinal)
        .Select(group => new ComponentSummary(
            group.Key,
            group.Count(),
            Round(group.Count() * 100m / values.Count)!.Value))
        .OrderByDescending(static value => value.MatchRate)
        .ThenBy(static value => value.Code, StringComparer.Ordinal)
        .ToArray();
}

static decimal Median(decimal[] values)
{
    var sorted = values.Order().ToArray();
    if (sorted.Length == 0) return 0m;
    var middle = sorted.Length / 2;
    return sorted.Length % 2 == 1
        ? sorted[middle]
        : (sorted[middle - 1] + sorted[middle]) / 2m;
}

static decimal? Round(decimal? value) => value is null ? null : Math.Round(value.Value, 3);

static async Task WriteEventsAsync(string path, IEnumerable<DailyPairEvent> values)
{
    await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
    await writer.WriteLineAsync(
        "event_key,symbol,pivot_type,signal_date,signal_at,pair_price,pair_code,pair_kind,generation,is_active,invalidated_at,invalidation_reason,source_row_hash,algorithm_version");
    foreach (var value in values.OrderBy(static value => value.SignalDate)
                 .ThenBy(static value => value.Symbol).ThenBy(static value => value.EventKey))
        await writer.WriteLineAsync(string.Join(',',
            value.EventKey, value.Symbol, value.PivotType, value.SignalDate,
            value.SignalAt.ToString("O"), Format(value.PairPrice), value.PairCode,
            value.PairKind, value.Generation, value.IsActive ? 1 : 0,
            value.InvalidatedAt?.ToString("yyyy-MM-dd") ?? string.Empty,
            value.InvalidationReason ?? string.Empty, value.SourceRowHash, dailyPairAlgorithm));
}

static async Task WriteResultsAsync(string path, IEnumerable<BottomResult> values)
{
    await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
    await writer.WriteLineAsync(
        "event_key,symbol,signal_date,pair_price,generation,current_active,invalidated_at,calculation_status,signal,score,trend_gate,matched_components,daily_bars,data_as_of,has_20_days,outcome_through,return_5_pct,return_10_pct,return_20_pct,mfe_20_pct,mae_20_pct,first_barrier");
    foreach (var value in values.OrderBy(static value => value.SignalDate)
                 .ThenBy(static value => value.Symbol).ThenBy(static value => value.EventKey))
        await writer.WriteLineAsync(string.Join(',',
            value.EventKey, value.Symbol, value.SignalDate, Format(value.PairPrice), value.Generation,
            value.CurrentIsActive ? 1 : 0,
            value.InvalidatedAt?.ToString("yyyy-MM-dd") ?? string.Empty,
            value.CalculationStatus, value.Signal, value.Score,
            value.TrendGatePassed ? 1 : 0, value.MatchedComponents,
            value.DailyBarCount, value.DataAsOf?.ToString("O") ?? string.Empty,
            value.Has20Days ? 1 : 0,
            value.OutcomeThrough?.ToString("yyyy-MM-dd") ?? string.Empty,
            Format(value.Return5), Format(value.Return10), Format(value.Return20),
            Format(value.Mfe20), Format(value.Mae20), value.FirstBarrier));
}

static string BuildReport(BacktestSummary value)
{
    var builder = new StringBuilder();
    builder.AppendLine("# 2021年日K对子底与波段信号离线回测");
    builder.AppendLine();
    builder.AppendLine($"- 日K对子算法：`{value.PairAlgorithmVersion}`");
    builder.AppendLine($"- 波段算法：`{value.WaveAlgorithmVersion}`");
    builder.AppendLine($"- 日期：{value.DateFrom} 至 {value.DateTo}");
    builder.AppendLine($"- 股票：{value.Symbols}；读取官方日K：{value.DailyBars}");
    builder.AppendLine($"- 目标区间历史可用股票日K守恒：{value.EligibleTargetBars}/{value.EligibleTargetBars}");
    builder.AppendLine($"- 日K对子事件：{value.PairEvents}（顶部 {value.TopEvents} / 底部 {value.BottomEvents}）");
    builder.AppendLine($"- 独立底部样本：{value.IndependentSamples}；完成评分且有20日结果：{value.EligibleIndependentSamples}");
    builder.AppendLine($"- 趋势门禁通过：{value.TrendGatePassedSamples}；70分以上：{value.ScoreAtLeast70Samples}；最高分：{value.MaximumScore}");
    builder.AppendLine();
    builder.AppendLine("| 分组 | 样本 | 5日中位% | 10日中位% | 20日中位% | 20日均值% | 20日胜率% | MFE20中位% | MAE20中位% | 先涨5%比例% |");
    builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var cohort in value.Cohorts)
        builder.AppendLine($"| {cohort.Name} | {cohort.Count} | {cohort.MedianReturn5} | {cohort.MedianReturn10} | {cohort.MedianReturn20} | {cohort.MeanReturn20} | {cohort.PositiveReturn20Rate} | {cohort.MedianMfe20} | {cohort.MedianMae20} | {cohort.Plus5FirstRate} |");
    builder.AppendLine();
    builder.AppendLine("> 日K对子信号在当日收盘后形成；评分包含该根已完成日K，收益从下一根可交易日日K开盘计算。本回测不构成投资建议。");
    return builder.ToString();
}

static string Format(decimal? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

sealed record SnapshotManifest(
    string SnapshotVersion,
    string Stage,
    string CalculationMode,
    bool DailyOnly,
    string TargetFrom,
    string TargetTo,
    IReadOnlyList<string> Symbols,
    string EligibilitySha256,
    int RequestedBatchCount,
    IReadOnlyDictionary<string, BatchManifest> CompletedBatches,
    IsolationManifest Isolation);

sealed record BatchManifest(string Sha256, int Symbols, long Bars);
sealed record IsolationManifest(bool WebApiClient, bool MySqlClient, bool RedisClient, bool Migration);

sealed record DailyBarRow(
    string Symbol,
    string Frequency,
    string TradingDate,
    DateTime Bob,
    DateTime Eob,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    decimal? PreClose,
    long Volume,
    decimal Amount,
    string SourceRowHash)
{
    public DailyBar ToDailyBar()
    {
        var tradingDate = DateOnly.Parse(TradingDate, CultureInfo.InvariantCulture);
        if (SourceRowHash.Length != 64 || HighPrice < Math.Max(OpenPrice, ClosePrice) ||
            LowPrice > Math.Min(OpenPrice, ClosePrice) || Volume < 0 || Amount < 0)
            throw new InvalidDataException($"日K质量失败: {Symbol}/{TradingDate}");
        if (Bob != tradingDate.ToDateTime(new TimeOnly(9, 30)) ||
            Eob != tradingDate.ToDateTime(new TimeOnly(15, 0)))
            throw new InvalidDataException($"日K时间语义失败: {Symbol}/{TradingDate}");
        return new DailyBar(Symbol, tradingDate, OpenPrice, HighPrice, LowPrice,
            ClosePrice, PreClose, Volume, Amount, SourceRowHash);
    }
}

sealed record DailyBar(
    string Symbol,
    DateOnly TradingDate,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    decimal? PreClose,
    long Volume,
    decimal Amount,
    string SourceRowHash)
{
    public PairTrendBar ToPairTrendBar()
    {
        var day = TradingDate.ToDateTime(TimeOnly.MinValue);
        return new PairTrendBar(Symbol, "1d", day, day.AddHours(9).AddMinutes(30),
            day.AddHours(15), OpenPrice, HighPrice, LowPrice, ClosePrice,
            PreClose, Volume, Amount, SourceRowHash);
    }
}

sealed class DailyPairEvent(
    string eventKey,
    string symbol,
    string pivotType,
    DateOnly signalDate,
    DateTime signalAt,
    decimal pairPrice,
    int pairCode,
    string pairKind,
    int generation,
    bool isActive,
    DateOnly? invalidatedAt,
    string? invalidationReason,
    string sourceRowHash)
{
    public string EventKey { get; } = eventKey;
    public string Symbol { get; } = symbol;
    public string PivotType { get; } = pivotType;
    public DateOnly SignalDate { get; } = signalDate;
    public DateTime SignalAt { get; } = signalAt;
    public decimal PairPrice { get; } = pairPrice;
    public int PairCode { get; } = pairCode;
    public string PairKind { get; } = pairKind;
    public int Generation { get; } = generation;
    public bool IsActive { get; set; } = isActive;
    public DateOnly? InvalidatedAt { get; set; } = invalidatedAt;
    public string? InvalidationReason { get; set; } = invalidationReason;
    public string SourceRowHash { get; } = sourceRowHash;
}

sealed record BottomResult(
    string EventKey,
    string Symbol,
    DateOnly SignalDate,
    decimal PairPrice,
    int Generation,
    bool CurrentIsActive,
    DateOnly? InvalidatedAt,
    string CalculationStatus,
    string Signal,
    int Score,
    bool TrendGatePassed,
    string MatchedComponents,
    int DailyBarCount,
    DateTime? DataAsOf,
    bool Has20Days,
    DateOnly? OutcomeThrough,
    decimal? Return5,
    decimal? Return10,
    decimal? Return20,
    decimal? Mfe20,
    decimal? Mae20,
    string FirstBarrier)
{
    public static BottomResult Create(
        DailyPairEvent pairEvent,
        WaveBottomEvaluation evaluation,
        IReadOnlyList<DailyBar> future)
    {
        var has20 = future.Count >= 20;
        var matched = string.Join('|', evaluation.Components
            .Where(static value => value.Matched).Select(static value => value.Code));
        if (future.Count == 0)
            return new BottomResult(
                pairEvent.EventKey, pairEvent.Symbol, pairEvent.SignalDate,
                pairEvent.PairPrice, pairEvent.Generation, pairEvent.IsActive,
                pairEvent.InvalidatedAt, evaluation.CalculationStatus,
                evaluation.Signal, evaluation.Score, evaluation.TrendGatePassed,
                matched, evaluation.DailyBarCount, evaluation.DataAsOf,
                false, null, null, null, null, null, null, "NONE");
        var baseline = future[0].OpenPrice;
        decimal? Return(int days) => future.Count < days
            ? null
            : (future[days - 1].ClosePrice / baseline - 1m) * 100m;
        decimal? mfe = has20
            ? (future.Take(20).Max(static value => value.HighPrice) / baseline - 1m) * 100m
            : null;
        decimal? mae = has20
            ? (future.Take(20).Min(static value => value.LowPrice) / baseline - 1m) * 100m
            : null;
        return new BottomResult(
            pairEvent.EventKey, pairEvent.Symbol, pairEvent.SignalDate,
            pairEvent.PairPrice, pairEvent.Generation, pairEvent.IsActive,
            pairEvent.InvalidatedAt, evaluation.CalculationStatus,
            evaluation.Signal, evaluation.Score, evaluation.TrendGatePassed,
            matched, evaluation.DailyBarCount, evaluation.DataAsOf,
            has20, has20 ? future[19].TradingDate : future[^1].TradingDate,
            Return(5), Return(10), Return(20), mfe, mae,
            has20 ? ResolveFirstBarrier(future.Take(20), baseline) : "NONE");
    }

    private static string ResolveFirstBarrier(IEnumerable<DailyBar> values, decimal baseline)
    {
        foreach (var value in values)
        {
            var plus = value.HighPrice >= baseline * 1.05m;
            var minus = value.LowPrice <= baseline * 0.95m;
            if (plus && minus) return "AMBIGUOUS";
            if (plus) return "PLUS5";
            if (minus) return "MINUS5";
        }
        return "NONE";
    }
}

sealed record ComponentSummary(string Code, int Count, decimal MatchRate);
sealed record CohortSummary(
    string Name,
    int Count,
    decimal? MedianReturn5,
    decimal? MedianReturn10,
    decimal? MedianReturn20,
    decimal? MeanReturn20,
    decimal? PositiveReturn20Rate,
    decimal? MedianMfe20,
    decimal? MedianMae20,
    decimal? Plus5FirstRate);

sealed record BacktestSummary(
    string PairAlgorithmVersion,
    string WaveAlgorithmVersion,
    DateOnly DateFrom,
    DateOnly DateTo,
    int Symbols,
    long DailyBars,
    long EligibleTargetBars,
    int PairEvents,
    int TopEvents,
    int BottomEvents,
    int ScoredBottomEvents,
    int IndependentSamples,
    int EligibleIndependentSamples,
    int TrendGatePassedSamples,
    int ScoreAtLeast70Samples,
    int MaximumScore,
    string SnapshotManifestSha256,
    IReadOnlyCollection<ComponentSummary> ComponentMatches,
    IReadOnlyCollection<CohortSummary> Cohorts);
