using System.Security.Cryptography;
using System.Text;
using AStockMonitor.Domain.Analytics;

namespace AStockMonitor.Application.Analytics;

/// <summary>
/// V3 对子顶底确定性状态机：5m 发现，30m/60m/1d 同方向同价格逐级升级，
/// 后续严格突破则失效。历史回放只用已关闭的官方 K 线，不使用未来数据。
/// </summary>
public sealed class PairTrendV3Engine(PairTrendOptions options)
{
    private readonly PairTrendOptions _options = Validate(options);

    /// <summary>按事件时间回放一只股票的四周期 K 线。</summary>
    public PairTrendSymbolResult Replay(
        string symbol,
        string? symbolName,
        IReadOnlyDictionary<string, IReadOnlyList<PairTrendBar>> barsByFrequency,
        DateOnly dateFrom,
        DateOnly dateTo)
    {
        var timeline = barsByFrequency
            .SelectMany(static pair => pair.Value)
            .Where(bar => DateOnly.FromDateTime(bar.TradingDate) >= dateFrom &&
                          DateOnly.FromDateTime(bar.TradingDate) <= dateTo)
            .Select(static bar => new TimelineBar(bar, EffectiveEob(bar)))
            .OrderBy(static item => item.EffectiveEob)
            .ThenBy(static item => FrequencyRank(item.Bar.Frequency))
            .ToArray();

        var events = new List<EventState>();
        var active = new Dictionary<(PairPivotType Pivot, long Ticks), EventState>();
        var generations = new Dictionary<(PairPivotType Pivot, long Ticks), int>();

        foreach (var item in timeline)
        {
            var frequency = NormalizeFrequency(item.Bar.Frequency);
            if (frequency == "5m")
            {
                InvalidateBrokenLevels(item, active);
                Discover(item, PairPivotType.Top, item.Bar.HighPrice, symbol, symbolName,
                    active, generations, events);
                Discover(item, PairPivotType.Bottom, item.Bar.LowPrice, symbol, symbolName,
                    active, generations, events);
                continue;
            }

            Promote(item, active);
        }

        return new PairTrendSymbolResult(
            symbol,
            timeline.LongLength,
            events.Select(ToResult).OrderBy(static item => item.FirstSeenAt).ToArray());
    }

    private void InvalidateBrokenLevels(
        TimelineBar item,
        IDictionary<(PairPivotType Pivot, long Ticks), EventState> active)
    {
        foreach (var pair in active.ToArray())
        {
            var state = pair.Value;
            var breakPrice = state.PivotType == PairPivotType.Top
                ? item.Bar.HighPrice
                : item.Bar.LowPrice;
            var broken = state.PivotType == PairPivotType.Top
                ? breakPrice > state.Match.Price
                : breakPrice < state.Match.Price;
            if (!broken || item.EffectiveEob <= state.DiscoveredAt)
                continue;

            var previous = state.Stage;
            state.Stage = PairTrendStage.Invalidated;
            state.IsActive = false;
            state.InvalidatedAt = item.EffectiveEob;
            state.InvalidatedPrice = breakPrice;
            state.InvalidationReason = state.PivotType == PairPivotType.Top
                ? "HIGHER_PRICE_BREAK"
                : "LOWER_PRICE_BREAK";
            state.LastSeenAt = item.EffectiveEob;
            state.Lifecycles.Add(Lifecycle(
                state, previous, PairTrendStage.Invalidated, item, breakPrice,
                state.InvalidationReason, previous >= PairTrendStage.Observing));
            active.Remove(pair.Key);
        }
    }

    private void Discover(
        TimelineBar item,
        PairPivotType pivotType,
        decimal price,
        string symbol,
        string? symbolName,
        IDictionary<(PairPivotType Pivot, long Ticks), EventState> active,
        IDictionary<(PairPivotType Pivot, long Ticks), int> generations,
        ICollection<EventState> events)
    {
        var match = PairPriceMatcher.Match(price, _options.PriceTick, _options.IncludeRound00);
        if (match is null)
            return;

        var key = (pivotType, match.PriceTicks);
        if (!active.TryGetValue(key, out var state))
        {
            var generation = (generations.TryGetValue(key, out var previousGeneration)
                ? previousGeneration
                : 0) + 1;
            generations[key] = generation;
            var identity = string.Join('|', symbol, pivotType, match.PriceTicks,
                item.EffectiveEob.ToString("O"), generation, _options.AlgorithmVersion);
            state = new EventState(
                Hash(identity), symbol, symbolName, pivotType, match, generation,
                item.Bar.Bob, item.Bar.Eob, item.EffectiveEob);
            state.Lifecycles.Add(Lifecycle(
                state, null, PairTrendStage.Discovered, item, price,
                "FIVE_MINUTE_DISCOVERY", false));
            active.Add(key, state);
            events.Add(state);
        }

        // 同一活动价位再次在 5m 出现时只追加证据，主事件保持一条。
        if (state.Hits.All(hit => hit.Frequency != "5m" || hit.Eob != item.Bar.Eob))
            state.Hits.Add(CreateHit(state, item, PairTrendStage.Discovered, false,
                "FIVE_MINUTE_DISCOVERY"));
        state.LastSeenAt = item.EffectiveEob;
    }

    private void Promote(
        TimelineBar item,
        IReadOnlyDictionary<(PairPivotType Pivot, long Ticks), EventState> active)
    {
        var frequency = NormalizeFrequency(item.Bar.Frequency);
        var transition = frequency switch
        {
            "30m" => (From: PairTrendStage.Discovered, To: PairTrendStage.Observing,
                Reason: "SAME_PRICE_30M"),
            "60m" => (From: PairTrendStage.Observing, To: PairTrendStage.Focus,
                Reason: "SAME_PRICE_60M"),
            "1d" => (From: PairTrendStage.Focus, To: PairTrendStage.Established,
                Reason: "SAME_PRICE_1D"),
            _ => default
        };
        if (transition.Reason is null)
            return;

        foreach (var state in active.Values.ToArray())
        {
            if (state.Stage != transition.From || item.EffectiveEob < state.DiscoveredAt)
                continue;
            var triggerPrice = state.PivotType == PairPivotType.Top
                ? item.Bar.HighPrice
                : item.Bar.LowPrice;
            var match = PairPriceMatcher.Match(
                triggerPrice, _options.PriceTick, _options.IncludeRound00);
            if (match?.PriceTicks != state.Match.PriceTicks)
                continue;

            var previous = state.Stage;
            state.Stage = transition.To;
            state.LastSeenAt = item.EffectiveEob;
            if (transition.To == PairTrendStage.Observing)
                state.ObservedAt = item.EffectiveEob;
            else if (transition.To == PairTrendStage.Focus)
                state.FocusedAt = item.EffectiveEob;
            else if (transition.To == PairTrendStage.Established)
                state.EstablishedAt = item.EffectiveEob;
            state.Hits.Add(CreateHit(state, item, transition.To, true, transition.Reason));
            state.Lifecycles.Add(Lifecycle(
                state, previous, transition.To, item, triggerPrice,
                transition.Reason, true));
        }
    }

    private PairTrendHitResult CreateHit(
        EventState state,
        TimelineBar item,
        PairTrendStage stage,
        bool promotion,
        string reason)
    {
        var bar = item.Bar;
        var frequency = NormalizeFrequency(bar.Frequency);
        var identity = string.Join('|', state.EventKey, frequency, bar.Eob.ToString("O"),
            stage, _options.AlgorithmVersion);
        return new PairTrendHitResult(
            Hash(identity), state.Symbol, frequency, bar.TradingDate, bar.Bob, bar.Eob,
            item.EffectiveEob, promotion ? item.EffectiveEob : null,
            state.PivotType, promotion ? PairHitStatus.Confirmed : PairHitStatus.Candidate,
            state.Match.Price, state.Match.PriceTicks, state.Match.PairCode, state.Match.Kind,
            state.PivotType == PairPivotType.Top ? "HIGH" : "LOW",
            PairTrendDirection.Unknown, 0m, 0m, 0m, 0m, bar.PreClose,
            bar.OpenPrice, bar.HighPrice, bar.LowPrice, bar.ClosePrice,
            Math.Max(0, bar.Volume), Math.Max(0, bar.Amount), false, 0m, 0m, 0m,
            StageScore(stage), reason, bar.SourceRowHash, _options.AlgorithmVersion,
            stage, promotion);
    }

    private static PairTrendLifecycleResult Lifecycle(
        EventState state,
        PairTrendStage? from,
        PairTrendStage to,
        TimelineBar item,
        decimal price,
        string reason,
        bool notify)
    {
        var identity = string.Join('|', state.EventKey, from, to,
            item.EffectiveEob.ToString("O"), reason, item.Bar.SourceRowHash);
        return new PairTrendLifecycleResult(
            Hash(identity), from, to, item.EffectiveEob,
            NormalizeFrequency(item.Bar.Frequency), price, reason,
            item.Bar.SourceRowHash, notify);
    }

    private PairTrendEventResult ToResult(EventState state)
    {
        var frequencies = state.Hits.Select(static hit => hit.Frequency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(FrequencyRank).ToArray();
        var status = state.Stage == PairTrendStage.Invalidated
            ? PairEventStatus.Invalidated
            : state.Stage == PairTrendStage.Discovered
                ? PairEventStatus.Candidate
                : PairEventStatus.Confirmed;
        var promotions = state.Hits.Count(static hit => hit.IsPromotion);
        return new PairTrendEventResult(
            state.EventKey, state.Symbol, state.SymbolName, state.PivotType, status,
            state.DiscoveredAt, state.LastSeenAt, state.ObservedAt,
            state.Match.Price, state.Match.PairCode, state.Match.Kind,
            frequencies.Aggregate(0, static (mask, frequency) => mask | FrequencyMask(frequency)),
            string.Join(',', frequencies), frequencies.OrderByDescending(FrequencyRank).First(),
            frequencies.Length, state.Hits.Count, promotions,
            state.Stage == PairTrendStage.Invalidated ? 1 : 0,
            state.Stage == PairTrendStage.Discovered ? state.Hits.Count : 0,
            state.Hits.Count(hit => hit.PairKind == PairPriceKind.Round00),
            state.Hits.Count(hit => hit.PairKind == PairPriceKind.DoubleDigit),
            StageScore(state.Stage == PairTrendStage.Invalidated
                ? StrongestStage(state) : state.Stage),
            0m, _options.AlgorithmVersion, state.Hits,
            state.Stage, state.Match.PriceTicks, state.Generation, state.IsActive,
            state.ObservedAt, state.FocusedAt, state.EstablishedAt,
            state.InvalidatedAt, state.InvalidatedPrice, state.InvalidationReason,
            state.RootFiveMinuteBob, state.RootFiveMinuteEob, state.Lifecycles);
    }

    private static PairTrendStage StrongestStage(EventState state)
    {
        if (state.EstablishedAt is not null) return PairTrendStage.Established;
        if (state.FocusedAt is not null) return PairTrendStage.Focus;
        if (state.ObservedAt is not null) return PairTrendStage.Observing;
        return PairTrendStage.Discovered;
    }

    private static decimal StageScore(PairTrendStage stage) => stage switch
    {
        PairTrendStage.Established => 1m,
        PairTrendStage.Focus => 0.75m,
        PairTrendStage.Observing => 0.50m,
        _ => 0.25m
    };

    private static DateTime EffectiveEob(PairTrendBar bar)
    {
        if (NormalizeFrequency(bar.Frequency) != "1d")
            return bar.Eob;
        // 部分日线源把 eob 记为交易日 00:00，回放必须按 A 股收盘时点排序。
        return bar.TradingDate.Date.AddHours(15);
    }

    public static string NormalizeFrequency(string frequency) =>
        PairTrendAnalyzer.NormalizeFrequency(frequency);

    private static int FrequencyRank(string frequency) => NormalizeFrequency(frequency) switch
    {
        "5m" => 1, "30m" => 2, "60m" => 3, "1d" => 4, _ => 99
    };

    private static int FrequencyMask(string frequency) => NormalizeFrequency(frequency) switch
    {
        "5m" => 1, "30m" => 2, "60m" => 4, "1d" => 8, _ => 0
    };

    private static string Hash(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static PairTrendOptions Validate(PairTrendOptions value)
    {
        value.Validate();
        return value;
    }

    private sealed record TimelineBar(PairTrendBar Bar, DateTime EffectiveEob);

    private sealed class EventState(
        string eventKey,
        string symbol,
        string? symbolName,
        PairPivotType pivotType,
        PairPriceMatch match,
        int generation,
        DateTime rootFiveMinuteBob,
        DateTime rootFiveMinuteEob,
        DateTime discoveredAt)
    {
        public string EventKey { get; } = eventKey;
        public string Symbol { get; } = symbol;
        public string? SymbolName { get; } = symbolName;
        public PairPivotType PivotType { get; } = pivotType;
        public PairPriceMatch Match { get; } = match;
        public int Generation { get; } = generation;
        public DateTime RootFiveMinuteBob { get; } = rootFiveMinuteBob;
        public DateTime RootFiveMinuteEob { get; } = rootFiveMinuteEob;
        public DateTime DiscoveredAt { get; } = discoveredAt;
        public DateTime LastSeenAt { get; set; } = discoveredAt;
        public PairTrendStage Stage { get; set; } = PairTrendStage.Discovered;
        public bool IsActive { get; set; } = true;
        public DateTime? ObservedAt { get; set; }
        public DateTime? FocusedAt { get; set; }
        public DateTime? EstablishedAt { get; set; }
        public DateTime? InvalidatedAt { get; set; }
        public decimal? InvalidatedPrice { get; set; }
        public string? InvalidationReason { get; set; }
        public List<PairTrendHitResult> Hits { get; } = [];
        public List<PairTrendLifecycleResult> Lifecycles { get; } = [];
    }
}
