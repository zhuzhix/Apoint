using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Domain.Analytics;

var arguments = ParseArguments(args);
var eventPath = RequireFile(arguments, "events");
var dailyPath = RequireFile(arguments, "daily");
var outputDirectory = Path.GetFullPath(arguments.GetValueOrDefault("output", "wave-backtest-output"));
Directory.CreateDirectory(outputDirectory);

var events = LoadEvents(eventPath);
var dailyBySymbol = LoadDailyBars(dailyPath);
var scorer = new WaveBottomScorer(new WaveBottomOptions());
var results = new List<EventResult>(events.Count);

foreach (var pairEvent in events)
{
    if (!dailyBySymbol.TryGetValue(pairEvent.Symbol, out var symbolBars))
    {
        results.Add(EventResult.Missing(pairEvent, "NO_DAILY_DATA"));
        continue;
    }

    // The signal is calculated from completed bars strictly before the FOCUS
    // date. The next available daily open is the outcome observation baseline.
    var input = symbolBars
        .Where(bar => bar.TradingDate < DateOnly.FromDateTime(pairEvent.FocusedAt))
        .TakeLast(120)
        .Select(static bar => bar.ToPairTrendBar())
        .ToArray();
    var evaluation = scorer.Evaluate(input);
    var future = symbolBars
        .Where(bar => bar.TradingDate > DateOnly.FromDateTime(pairEvent.FocusedAt))
        .Take(20)
        .ToArray();
    results.Add(EventResult.Create(pairEvent, evaluation, future));
}

var primary = BuildIndependentSample(results);
var eligiblePrimary = primary
    .Where(static item => item.CalculationStatus == "COMPLETED" && item.Has20Days)
    .ToArray();
var cohorts = new[]
{
    Summarize("ALL_FOCUS", eligiblePrimary),
    Summarize("NONE", eligiblePrimary.Where(static item => item.Signal == "NONE").ToArray()),
    Summarize("CANDIDATE", eligiblePrimary.Where(static item => item.Signal == "CANDIDATE").ToArray()),
    Summarize("STRONG", eligiblePrimary.Where(static item => item.Signal == "STRONG").ToArray()),
    Summarize("CANDIDATE_OR_STRONG", eligiblePrimary.Where(
        static item => item.Signal is "CANDIDATE" or "STRONG").ToArray())
};

var summary = new BacktestSummary(
    WaveBottomOptions.CurrentAlgorithmVersion,
    events.Count,
    results.Count(static item => item.CalculationStatus == "COMPLETED"),
    results.Count(static item => item.CalculationStatus == "INSUFFICIENT_DATA"),
    results.Count(static item => item.Has20Days),
    primary.Count,
    eligiblePrimary.Length,
    events.Min(static item => item.FocusedAt),
    events.Max(static item => item.FocusedAt),
    Sha256(eventPath),
    Sha256(dailyPath),
    eligiblePrimary.Count(static item => item.TrendGatePassed),
    eligiblePrimary.Count(static item => item.Score >= 60),
    eligiblePrimary.Max(static item => item.Score),
    BuildComponentSummary(eligiblePrimary),
    cohorts);

await File.WriteAllTextAsync(
    Path.Combine(outputDirectory, "summary.json"),
    JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
await WriteCsvAsync(Path.Combine(outputDirectory, "event-results.csv"), results);
await File.WriteAllTextAsync(
    Path.Combine(outputDirectory, "report.md"),
    BuildMarkdown(summary));

Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

static IReadOnlyDictionary<string, string> ParseArguments(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("参数格式必须为 --name value。");
        result[values[index][2..]] = values[index + 1];
    }
    return result;
}

static string RequireFile(IReadOnlyDictionary<string, string> arguments, string name)
{
    if (!arguments.TryGetValue(name, out var value) || !File.Exists(value))
        throw new FileNotFoundException($"缺少 --{name} 回测输入文件。", value);
    return Path.GetFullPath(value);
}

static List<FocusEvent> LoadEvents(string path)
{
    var rows = new List<FocusEvent>();
    var ids = new HashSet<long>();
    foreach (var line in File.ReadLines(path))
    {
        var fields = line.Split('\t');
        if (fields.Length != 10) throw new InvalidDataException("FOCUS事件快照列数不是10。" );
        var row = new FocusEvent(
            long.Parse(fields[0], CultureInfo.InvariantCulture), fields[1], fields[2],
            DateTime.Parse(fields[3], CultureInfo.InvariantCulture),
            DateTime.Parse(fields[4], CultureInfo.InvariantCulture),
            int.Parse(fields[5], CultureInfo.InvariantCulture), fields[6], fields[7] == "1",
            NullableDateTime(fields[8]), NullableDateTime(fields[9]));
        if (!ids.Add(row.Id)) throw new InvalidDataException($"事件ID重复: {row.Id}");
        if (row.FocusedAt < row.RootFiveMinuteEob)
            throw new InvalidDataException($"事件 {row.Id} 的FOCUS时间早于根5分钟K线。" );
        rows.Add(row);
    }
    if (rows.Count == 0) throw new InvalidDataException("FOCUS事件快照为空。" );
    return rows.OrderBy(static row => row.FocusedAt).ThenBy(static row => row.Id).ToList();
}

static Dictionary<string, List<DailyBar>> LoadDailyBars(string path)
{
    var result = new Dictionary<string, List<DailyBar>>(StringComparer.OrdinalIgnoreCase);
    var unique = new HashSet<(string Symbol, DateOnly TradingDate)>();
    foreach (var line in File.ReadLines(path))
    {
        var fields = line.Split('\t');
        if (fields.Length != 10) throw new InvalidDataException("日K快照列数不是10。" );
        var row = new DailyBar(
            fields[0], DateOnly.Parse(fields[1], CultureInfo.InvariantCulture),
            Decimal(fields[2]), Decimal(fields[3]), Decimal(fields[4]), Decimal(fields[5]),
            IsNull(fields[6]) ? null : Decimal(fields[6]),
            long.Parse(fields[7], CultureInfo.InvariantCulture), Decimal(fields[8]), fields[9]);
        if (!unique.Add((row.Symbol.ToUpperInvariant(), row.TradingDate)))
            throw new InvalidDataException($"日K重复: {row.Symbol}/{row.TradingDate}" );
        if (row.RowHash.Length != 64 || row.HighPrice < Math.Max(row.OpenPrice, row.ClosePrice) ||
            row.LowPrice > Math.Min(row.OpenPrice, row.ClosePrice))
            throw new InvalidDataException($"日K质量失败: {row.Symbol}/{row.TradingDate}" );
        if (!result.TryGetValue(row.Symbol, out var values))
            result[row.Symbol] = values = [];
        values.Add(row);
    }
    foreach (var values in result.Values)
        values.Sort(static (left, right) => left.TradingDate.CompareTo(right.TradingDate));
    return result;
}

static List<EventResult> BuildIndependentSample(IEnumerable<EventResult> source)
{
    var output = new List<EventResult>();
    foreach (var group in source.GroupBy(static item => item.Symbol, StringComparer.OrdinalIgnoreCase))
    {
        DateOnly? blockedThrough = null;
        foreach (var item in group.OrderBy(static value => value.FocusedAt).ThenBy(static value => value.EventId))
        {
            var focusDate = DateOnly.FromDateTime(item.FocusedAt);
            if (blockedThrough is not null && focusDate <= blockedThrough) continue;
            output.Add(item);
            if (item.OutcomeThrough is not null) blockedThrough = item.OutcomeThrough;
        }
    }
    return output.OrderBy(static item => item.FocusedAt).ThenBy(static item => item.EventId).ToList();
}

static CohortSummary Summarize(string name, IReadOnlyCollection<EventResult> values)
{
    if (values.Count == 0) return new CohortSummary(name, 0, null, null, null, null, null, null, null, null, null);
    var return5 = values.Select(static item => item.Return5!.Value).ToArray();
    var return10 = values.Select(static item => item.Return10!.Value).ToArray();
    var return20 = values.Select(static item => item.Return20!.Value).ToArray();
    var ci = BootstrapMedianInterval(return20, 2_000, 20260819 + name.Length);
    return new CohortSummary(
        name, values.Count,
        Round(Median(return5)), Round(Median(return10)), Round(Median(return20)),
        Round(return20.Average()),
        Round(values.Count(static item => item.Return20 > 0m) * 100m / values.Count),
        Round(Median(values.Select(static item => item.Mfe20!.Value).ToArray())),
        Round(Median(values.Select(static item => item.Mae20!.Value).ToArray())),
        Round(values.Count(static item => item.FirstBarrier == "PLUS5") * 100m / values.Count),
        new ConfidenceInterval(Round(ci.Low), Round(ci.High)));
}

static IReadOnlyCollection<ComponentMatchSummary> BuildComponentSummary(
    IReadOnlyCollection<EventResult> values) => values
    .SelectMany(static item => item.MatchedComponents.Split(
        '|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    .GroupBy(static code => code, StringComparer.Ordinal)
    .Select(group => new ComponentMatchSummary(
        group.Key, group.Count(), Round(group.Count() * 100m / values.Count)!.Value))
    .OrderByDescending(static item => item.MatchRate)
    .ThenBy(static item => item.Code, StringComparer.Ordinal)
    .ToArray();

static (decimal Low, decimal High) BootstrapMedianInterval(decimal[] values, int iterations, int seed)
{
    var random = new Random(seed);
    var medians = new decimal[iterations];
    var sample = new decimal[values.Length];
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        for (var index = 0; index < sample.Length; index++)
            sample[index] = values[random.Next(values.Length)];
        medians[iteration] = Median(sample);
    }
    Array.Sort(medians);
    return (medians[(int)(iterations * 0.025m)], medians[(int)(iterations * 0.975m)]);
}

static decimal Median(decimal[] values)
{
    var sorted = values.Order().ToArray();
    if (sorted.Length == 0) return 0m;
    var middle = sorted.Length / 2;
    return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2m;
}

static async Task WriteCsvAsync(string path, IEnumerable<EventResult> values)
{
    await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
    await writer.WriteLineAsync("event_id,event_key,symbol,root_5m_eob,focused_at,generation,current_stage,current_active,calculation_status,signal,score,trend_gate,matched_components,daily_bars,data_as_of,has_20_days,outcome_through,return_5_pct,return_10_pct,return_20_pct,mfe_20_pct,mae_20_pct,first_barrier");
    foreach (var item in values)
    {
        await writer.WriteLineAsync(string.Join(',',
            item.EventId, Csv(item.EventKey), item.Symbol,
            item.RootFiveMinuteEob.ToString("O"), item.FocusedAt.ToString("O"), item.Generation,
            item.CurrentStage, item.CurrentIsActive ? 1 : 0, item.CalculationStatus,
            item.Signal, item.Score, item.TrendGatePassed ? 1 : 0,
            Csv(item.MatchedComponents), item.DailyBarCount,
            item.DataAsOf?.ToString("O") ?? string.Empty, item.Has20Days ? 1 : 0,
            item.OutcomeThrough?.ToString("yyyy-MM-dd") ?? string.Empty,
            Format(item.Return5), Format(item.Return10), Format(item.Return20),
            Format(item.Mfe20), Format(item.Mae20), item.FirstBarrier));
    }
}

static string BuildMarkdown(BacktestSummary summary)
{
    var builder = new StringBuilder();
    builder.AppendLine("# 重点底部波段信号回测结果");
    builder.AppendLine();
    builder.AppendLine($"- 算法：`{summary.AlgorithmVersion}`");
    builder.AppendLine($"- FOCUS区间：{summary.FocusFrom:yyyy-MM-dd} 至 {summary.FocusTo:yyyy-MM-dd}");
    builder.AppendLine($"- 原始事件：{summary.SourceEvents}");
    builder.AppendLine($"- 独立主样本：{summary.IndependentSamples}，其中完成评分且有20日观察：{summary.EligibleIndependentSamples}");
    builder.AppendLine($"- 数据不足：{summary.InsufficientDataEvents}");
    builder.AppendLine($"- 趋势门禁通过：{summary.TrendGatePassedSamples}/{summary.EligibleIndependentSamples}；达到候选阈值：{summary.ScoreAtLeastCandidateSamples}；最高分：{summary.MaximumScore}");
    builder.AppendLine();
    builder.AppendLine("| 分组 | 样本 | 5日收益中位% | 10日收益中位% | 20日收益中位% | 20日胜率% | MFE20中位% | MAE20中位% | 先涨5%比例% | 20日中位95%CI |");
    builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
    foreach (var cohort in summary.Cohorts)
        builder.AppendLine($"| {cohort.Name} | {cohort.Count} | {cohort.MedianReturn5} | {cohort.MedianReturn10} | {cohort.MedianReturn20} | {cohort.PositiveReturn20Rate} | {cohort.MedianMfe20} | {cohort.MedianMae20} | {cohort.Plus5FirstRate} | {cohort.MedianReturn20Confidence95?.Low}～{cohort.MedianReturn20Confidence95?.High} |");
    builder.AppendLine();
    builder.AppendLine("## 组件命中率（独立有效样本）");
    builder.AppendLine();
    foreach (var component in summary.ComponentMatches)
        builder.AppendLine($"- `{component.Code}`：{component.Count}（{component.MatchRate}%）");
    builder.AppendLine();
    builder.AppendLine("> 收益以FOCUS后的第一个可交易日日开盘为基准；该结果是研究回测，不构成交易建议。");
    return builder.ToString();
}

static bool IsNull(string value) => value is "\\N" or "NULL" or "";
static DateTime? NullableDateTime(string value) => IsNull(value)
    ? null : DateTime.Parse(value, CultureInfo.InvariantCulture);
static decimal Decimal(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
static decimal? Round(decimal? value) => value is null ? null : Math.Round(value.Value, 3);
static string Format(decimal? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

sealed record FocusEvent(
    long Id, string EventKey, string Symbol, DateTime RootFiveMinuteEob,
    DateTime FocusedAt, int Generation, string CurrentStage, bool CurrentIsActive,
    DateTime? EstablishedAt, DateTime? InvalidatedAt);

sealed record DailyBar(
    string Symbol, DateOnly TradingDate, decimal OpenPrice, decimal HighPrice,
    decimal LowPrice, decimal ClosePrice, decimal? PreClose, long Volume,
    decimal Amount, string RowHash)
{
    public PairTrendBar ToPairTrendBar()
    {
        var day = TradingDate.ToDateTime(TimeOnly.MinValue);
        return new PairTrendBar(Symbol, "1d", day, day.AddHours(9).AddMinutes(30),
            day.AddHours(15), OpenPrice, HighPrice, LowPrice, ClosePrice,
            PreClose, Volume, Amount, RowHash);
    }
}

sealed record EventResult(
    long EventId, string EventKey, string Symbol, DateTime RootFiveMinuteEob,
    DateTime FocusedAt, int Generation, string CurrentStage, bool CurrentIsActive,
    string CalculationStatus, string Signal, int Score, bool TrendGatePassed,
    string MatchedComponents, int DailyBarCount, DateTime? DataAsOf,
    bool Has20Days, DateOnly? OutcomeThrough,
    decimal? Return5, decimal? Return10, decimal? Return20,
    decimal? Mfe20, decimal? Mae20, string FirstBarrier)
{
    public static EventResult Missing(FocusEvent value, string status) => new(
        value.Id, value.EventKey, value.Symbol, value.RootFiveMinuteEob,
        value.FocusedAt, value.Generation, value.CurrentStage, value.CurrentIsActive,
        status, "NONE", 0, false, string.Empty, 0, null, false, null,
        null, null, null, null, null, "NONE");

    public static EventResult Create(
        FocusEvent value, WaveBottomEvaluation evaluation, DailyBar[] future)
    {
        var has20 = future.Length >= 20;
        if (future.Length == 0)
            return Missing(value, evaluation.CalculationStatus) with
            {
                Signal = evaluation.Signal,
                Score = evaluation.Score,
                TrendGatePassed = evaluation.TrendGatePassed,
                MatchedComponents = string.Join('|', evaluation.Components
                    .Where(static item => item.Matched).Select(static item => item.Code)),
                DailyBarCount = evaluation.DailyBarCount,
                DataAsOf = evaluation.DataAsOf
            };
        var baseOpen = future[0].OpenPrice;
        decimal? Return(int days) => future.Length < days
            ? null : (future[days - 1].ClosePrice / baseOpen - 1m) * 100m;
        decimal? mfe = has20
            ? (future.Take(20).Max(static bar => bar.HighPrice) / baseOpen - 1m) * 100m
            : null;
        decimal? mae = has20
            ? (future.Take(20).Min(static bar => bar.LowPrice) / baseOpen - 1m) * 100m
            : null;
        return new EventResult(
            value.Id, value.EventKey, value.Symbol, value.RootFiveMinuteEob,
            value.FocusedAt, value.Generation, value.CurrentStage, value.CurrentIsActive,
            evaluation.CalculationStatus, evaluation.Signal, evaluation.Score,
            evaluation.TrendGatePassed,
            string.Join('|', evaluation.Components
                .Where(static item => item.Matched).Select(static item => item.Code)),
            evaluation.DailyBarCount, evaluation.DataAsOf,
            has20, has20 ? future[19].TradingDate : future[^1].TradingDate,
            Return(5), Return(10), Return(20), mfe, mae,
            has20 ? ResolveFirstBarrier(future.Take(20), baseOpen) : "NONE");
    }

    private static string ResolveFirstBarrier(IEnumerable<DailyBar> bars, decimal baseline)
    {
        foreach (var bar in bars)
        {
            var plus = bar.HighPrice >= baseline * 1.05m;
            var minus = bar.LowPrice <= baseline * 0.95m;
            if (plus && minus) return "AMBIGUOUS";
            if (plus) return "PLUS5";
            if (minus) return "MINUS5";
        }
        return "NONE";
    }
}

sealed record ConfidenceInterval(decimal? Low, decimal? High);
sealed record ComponentMatchSummary(string Code, int Count, decimal MatchRate);
sealed record CohortSummary(
    string Name, int Count, decimal? MedianReturn5, decimal? MedianReturn10,
    decimal? MedianReturn20, decimal? MeanReturn20, decimal? PositiveReturn20Rate,
    decimal? MedianMfe20, decimal? MedianMae20, decimal? Plus5FirstRate,
    ConfidenceInterval? MedianReturn20Confidence95);
sealed record BacktestSummary(
    string AlgorithmVersion, int SourceEvents, int CompletedEvents,
    int InsufficientDataEvents, int EventsWith20Days, int IndependentSamples,
    int EligibleIndependentSamples, DateTime FocusFrom, DateTime FocusTo,
    string EventSnapshotSha256, string DailySnapshotSha256,
    int TrendGatePassedSamples, int ScoreAtLeastCandidateSamples, int MaximumScore,
    IReadOnlyCollection<ComponentMatchSummary> ComponentMatches,
    IReadOnlyCollection<CohortSummary> Cohorts);
