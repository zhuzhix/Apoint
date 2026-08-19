using AStockMonitor.Api.Models;
using AStockMonitor.Domain.Analytics;

namespace AStockMonitor.Api.Services;

/// <summary>
/// 交易日内的四周期 K 线工作集。原始 K 线只保留在 API 内存，日切时整体释放；
/// MySQL 只接收对子事件投影结果。
/// </summary>
public sealed class PairTrendCollectionSessionStore
{
    private static readonly StringComparer SymbolComparer = StringComparer.OrdinalIgnoreCase;
    private readonly object _sync = new();
    private Session? _session;

    public PairTrendCollectionPlanResponse BeginPlan(
        DateOnly tradingDate,
        IReadOnlyList<PairTrendCollectionSymbol> symbols,
        IReadOnlyList<PairTrendCollectionWindow> availableWindows)
    {
        lock (_sync)
        {
            if (_session is not null)
            {
                ExpireAbandonedPlans(_session);
                if (_session.TradingDate != tradingDate &&
                    (_session.InFlight || _session.Plans.Count > 0))
                {
                    return NoPlan(
                        tradingDate,
                        $"{_session.TradingDate:yyyy-MM-dd} 的采集或计算仍在执行，" +
                        $"拒绝切换到 {tradingDate:yyyy-MM-dd}；现有 cycle 未被清空。");
                }
            }

            if (_session is null || _session.TradingDate != tradingDate)
            {
                _session = new Session(tradingDate, symbols);
            }
            else if (!_session.Universe.Keys.ToHashSet(SymbolComparer)
                .SetEquals(symbols.Select(static item => item.Symbol)))
            {
                // 股票池发生变化，上一轮的全量完备性前提已不成立。只允许重新 bootstrap。
                _session = new Session(tradingDate, symbols);
            }

            if (_session.InFlight)
            {
                return NoPlan(_session, "上一轮对子计算仍在执行，禁止并发推进 K 线水位。");
            }
            if (_session.Plans.Count > 0)
            {
                return NoPlan(_session, "已有采集计划尚未完成；必须先完成或显式报告失败。");
            }

            var windows = new List<PairTrendCollectionWindow>();
            foreach (var candidate in availableWindows)
            {
                if (_session.Watermarks.TryGetValue(candidate.Frequency, out var watermark) &&
                    candidate.To <= watermark)
                {
                    continue;
                }

                var overlap = candidate.Frequency switch
                {
                    "5m" => TimeSpan.FromMinutes(5),
                    "30m" => TimeSpan.FromMinutes(30),
                    "60m" => TimeSpan.FromMinutes(60),
                    "1d" => TimeSpan.FromDays(1),
                    _ => throw new InvalidOperationException($"Unsupported frequency: {candidate.Frequency}")
                };
                var from = _session.Watermarks.TryGetValue(candidate.Frequency, out watermark)
                    ? Max(candidate.From, watermark - overlap)
                    : candidate.From;
                windows.Add(candidate with { From = from });
            }

            if (windows.Count == 0)
            {
                return NoPlan(_session, "没有新的已闭合 K 线。");
            }

            var cycleId = Guid.NewGuid().ToString("N");
            var mode = _session.Watermarks.Count == 0 ? "bootstrap" : "incremental";
            _session.Plans.Add(cycleId, new Cycle(
                cycleId, mode, _session.TradingDate, windows,
                _session.Universe.Keys.ToHashSet(SymbolComparer)));
                _session.Status = "collecting";
                _session.LastError = null;
                _session.LastErrorAt = null;
            return new PairTrendCollectionPlanResponse(
                true, null, cycleId, tradingDate, mode, windows,
                _session.Universe.Values.OrderBy(static item => item.Symbol, SymbolComparer).ToArray(),
                _session.Universe.Count);
        }
    }

    public int AcceptBatch(string cycleId, IReadOnlyList<PairTrendCollectedBar> bars)
    {
        if (bars.Count == 0)
            throw new ArgumentException("K 线批次不能为空。", nameof(bars));

        lock (_sync)
        {
            var (session, cycle) = RequireCycle(cycleId);
            if (cycle.Completed)
                throw new InvalidOperationException("该采集计划已完成，不能继续写入。");

            var windowByFrequency = cycle.Windows.ToDictionary(
                static item => item.Frequency, StringComparer.OrdinalIgnoreCase);
            // Validate the whole HTTP batch before mutating session state. If a
            // later row is invalid, no earlier row from the same request leaks
            // into the cycle and turns the next retry into a false duplicate.
            var pending = new Dictionary<(string Symbol, string Frequency, DateTime Eob), PairTrendBar>();
            foreach (var input in bars)
            {
                var symbol = NormalizeSymbol(input.Symbol);
                var frequency = NormalizeFrequency(input.Frequency);
                if (!session.Universe.ContainsKey(symbol))
                    throw new InvalidOperationException($"证券 {symbol} 不在本轮 API 下发的股票池内。");
                if (!windowByFrequency.TryGetValue(frequency, out var window))
                    throw new InvalidOperationException($"周期 {frequency} 不在本轮采集计划内。");
                if (input.Bob >= input.Eob || input.Eob > window.To || input.Eob < window.From)
                    throw new InvalidOperationException($"{symbol}/{frequency} 的 K 线时间不在计划窗口内。");
                if (!cycle.ExpectedEobsByFrequency[frequency].Contains(input.Eob))
                    throw new InvalidOperationException(
                        $"{symbol}/{frequency} 的 EOB {input.Eob:yyyy-MM-dd HH:mm:ss} " +
                        "不是计划窗口内的合法闭合时点。");
                if (input.HighPrice < input.LowPrice ||
                    input.HighPrice < Math.Max(input.OpenPrice, input.ClosePrice) ||
                    input.LowPrice > Math.Min(input.OpenPrice, input.ClosePrice) ||
                    input.OpenPrice < 0 || input.ClosePrice < 0 ||
                    input.Volume < 0 || string.IsNullOrWhiteSpace(input.SourceRowHash))
                    throw new InvalidOperationException($"{symbol}/{frequency} 的 K 线字段无效。");

                var candidate = new PairTrendBar(
                    symbol, frequency, session.TradingDate.ToDateTime(TimeOnly.MinValue), input.Bob,
                    input.Eob, input.OpenPrice, input.HighPrice, input.LowPrice, input.ClosePrice,
                    input.PreClose, input.Volume, input.Amount, input.SourceRowHash.Trim());
                var key = (symbol, frequency, input.Eob);
                if (pending.TryGetValue(key, out var sameBatch))
                {
                    RequireIdempotentDuplicate(sameBatch, candidate);
                    continue;
                }

                if (cycle.ReceivedEobsByFrequencyAndSymbol[frequency]
                        .TryGetValue(symbol, out var receivedEobs) &&
                    receivedEobs.Contains(input.Eob))
                {
                    if (!session.Bars.TryGetValue(symbol, out var existingByFrequency) ||
                        !existingByFrequency.TryGetValue(frequency, out var existingByEob) ||
                        !existingByEob.TryGetValue(input.Eob, out var existing))
                        throw new InvalidOperationException(
                            $"{symbol}/{frequency}/{input.Eob:yyyy-MM-dd HH:mm:ss} " +
                            "已登记接收但内存 K 线不存在，会话状态不一致。");
                    RequireIdempotentDuplicate(existing, candidate);
                    continue;
                }
                pending.Add(key, candidate);
            }

            foreach (var ((symbol, frequency, eob), candidate) in pending)
            {
                if (!session.Bars.TryGetValue(symbol, out var byFrequency))
                {
                    byFrequency = new Dictionary<string, SortedDictionary<DateTime, PairTrendBar>>(
                        StringComparer.OrdinalIgnoreCase);
                    session.Bars[symbol] = byFrequency;
                }
                if (!byFrequency.TryGetValue(frequency, out var byEob))
                {
                    byEob = new SortedDictionary<DateTime, PairTrendBar>();
                    byFrequency[frequency] = byEob;
                }
                byEob[eob] = candidate;
                if (!cycle.ReceivedEobsByFrequencyAndSymbol[frequency]
                    .TryGetValue(symbol, out var receivedEobs))
                {
                    receivedEobs = [];
                    cycle.ReceivedEobsByFrequencyAndSymbol[frequency][symbol] = receivedEobs;
                }
                receivedEobs.Add(eob);
            }
            return bars.Count;
        }
    }

    public PairTrendCollectionWorkItem Complete(
        string cycleId,
        PairTrendCollectionCompleteRequest completion)
    {
        lock (_sync)
        {
            var (session, cycle) = RequireCycle(cycleId);
            if (cycle.Completed || session.InFlight)
                throw new InvalidOperationException("该采集计划正在处理或已完成。");
            if (completion.Failures is { Count: > 0 })
            {
                session.Plans.Remove(cycleId);
                session.Status = "failed";
                session.LastError = "Python 采集端报告了失败，未推进任何 K 线水位。";
                session.LastErrorAt = DateTime.UtcNow;
                throw new InvalidOperationException(session.LastError);
            }

            var completed = completion.CompletedSymbols
                .Select(NormalizeSymbol)
                .ToHashSet(SymbolComparer);
            if (!completed.SetEquals(cycle.ExpectedSymbols))
                throw new InvalidOperationException("完成清单必须与 API 下发的股票池完全一致，不能以部分股票推进水位。");

            var sparseClaims = new Dictionary<(string Symbol, string Frequency),
                (HashSet<DateTime> MissingEobs, int Confirmations)>();
            var validatedSparseRemovals = new List<
                (string Symbol, string Frequency, HashSet<DateTime> MissingEobs)>();
            foreach (var claim in completion.SparseManifest ??
                     Array.Empty<PairTrendCollectionSparseManifest>())
            {
                if (claim is null)
                    throw new InvalidOperationException("稀疏 K 线证明不能包含空项。");
                var symbol = NormalizeSymbol(claim.Symbol);
                var frequency = NormalizeFrequency(claim.Frequency);
                if (!cycle.ExpectedSymbols.Contains(symbol))
                    throw new InvalidOperationException($"稀疏证明证券 {symbol} 不在本轮股票池内。");
                if (!cycle.ExpectedEobsByFrequency.TryGetValue(frequency, out var expectedEobs))
                    throw new InvalidOperationException($"稀疏证明周期 {frequency} 不在本轮计划内。");
                if (claim.Confirmations != 3)
                    throw new InvalidOperationException(
                        $"{symbol}/{frequency} 稀疏证明必须精确包含 3 次独立成功响应。");
                if (claim.MissingEobs is null || claim.MissingEobs.Count == 0)
                    throw new InvalidOperationException(
                        $"{symbol}/{frequency} 稀疏证明必须逐项声明缺失 EOB。");
                var missingEobs = claim.MissingEobs.ToHashSet();
                if (missingEobs.Count != claim.MissingEobs.Count)
                    throw new InvalidOperationException(
                        $"{symbol}/{frequency} 稀疏证明包含重复 EOB。");
                if (missingEobs.Any(eob => !expectedEobs.Contains(eob)))
                    throw new InvalidOperationException(
                        $"{symbol}/{frequency} 稀疏证明包含计划外 EOB。");
                if (!sparseClaims.TryAdd((symbol, frequency),
                        (missingEobs, claim.Confirmations)))
                    throw new InvalidOperationException(
                        $"{symbol}/{frequency} 存在重复稀疏证明项。");
            }

            foreach (var frequency in cycle.Windows.Select(static item => item.Frequency))
            {
                var expectedEobs = cycle.ExpectedEobsByFrequency[frequency];
                foreach (var symbol in cycle.ExpectedSymbols)
                {
                    var receivedEobs = cycle.ReceivedEobsByFrequencyAndSymbol[frequency]
                        .TryGetValue(symbol, out var actual)
                        ? actual
                        : [];
                    var missingEobs = expectedEobs.Except(receivedEobs).ToHashSet();
                    var key = (symbol, frequency);
                    if (missingEobs.Count == 0)
                    {
                        if (sparseClaims.Remove(key))
                            throw new InvalidOperationException(
                                $"{symbol}/{frequency} 数据完整却提交了未消费的稀疏证明。");
                        if (!receivedEobs.SetEquals(expectedEobs))
                            throw new InvalidOperationException(
                                $"{symbol}/{frequency} 收到计划外 EOB，拒绝完成。");
                        continue;
                    }

                    if (!sparseClaims.Remove(key, out var proof))
                    {
                        throw new InvalidOperationException(
                            $"{symbol}/{frequency} 必须精确收到计划内 {expectedEobs.Count} 个闭合 EOB，" +
                            $"实际为 {receivedEobs.Count} 且没有三次一致的稀疏证明；" +
                            "拒绝使用不完整数据进行对子计算。");
                    }
                    if (receivedEobs.Overlaps(proof.MissingEobs))
                        throw new InvalidOperationException(
                            $"{symbol}/{frequency} 稀疏证明与已接收 EOB 相交。");
                    var covered = receivedEobs.Concat(proof.MissingEobs).ToHashSet();
                    if (!proof.MissingEobs.SetEquals(missingEobs) ||
                        !covered.SetEquals(expectedEobs))
                        throw new InvalidOperationException(
                            $"{symbol}/{frequency} 稀疏证明不是实际缺口的精确补集。");
                    validatedSparseRemovals.Add((symbol, frequency, proof.MissingEobs));
                }
            }
            if (sparseClaims.Count > 0)
                throw new InvalidOperationException("完成请求包含未被计划缺口消费的稀疏证明。");

            // All completion gates have passed. Apply official sparse retractions
            // only now, as one locked mutation, so a failed manifest can never
            // delete a bar retained from an earlier overlap cycle.
            foreach (var (symbol, frequency, missingEobs) in validatedSparseRemovals)
            {
                if (!session.Bars.TryGetValue(symbol, out var byFrequency) ||
                    !byFrequency.TryGetValue(frequency, out var byEob))
                    continue;
                foreach (var eob in missingEobs)
                    byEob.Remove(eob);
            }

            cycle.Completed = true;
            session.InFlight = true;
            session.Status = "computing";
            return new PairTrendCollectionWorkItem(cycle.CycleId, session.TradingDate);
        }
    }

    public bool TryTakeSnapshot(string cycleId, out PairTrendCollectionSnapshot? snapshot)
    {
        lock (_sync)
        {
            snapshot = null;
            if (_session is null || !_session.Plans.TryGetValue(cycleId, out var cycle) || !cycle.Completed)
                return false;

            var symbols = new List<PairTrendCollectionSymbolSnapshot>(_session.Universe.Count);
            foreach (var symbol in _session.Universe.Values)
            {
                var barsByFrequency = new Dictionary<string, IReadOnlyList<PairTrendBar>>(
                    StringComparer.OrdinalIgnoreCase);
                if (_session.Bars.TryGetValue(symbol.Symbol, out var byFrequency))
                {
                    foreach (var pair in byFrequency)
                        barsByFrequency[pair.Key] = pair.Value.Values.ToArray();
                }
                symbols.Add(new PairTrendCollectionSymbolSnapshot(symbol.Symbol, symbol.Name, barsByFrequency));
            }
            snapshot = new PairTrendCollectionSnapshot(cycle.CycleId, _session.TradingDate,
                cycle.Windows, symbols);
            return true;
        }
    }

    public void FinishProcessing(string cycleId, bool succeeded, string? error = null)
    {
        lock (_sync)
        {
            if (_session is null || !_session.Plans.TryGetValue(cycleId, out var cycle))
                return;

            if (succeeded)
            {
                foreach (var window in cycle.Windows)
                    _session.Watermarks[window.Frequency] = window.To;
                _session.Status = "idle";
                _session.LastCompletedAt = DateTime.UtcNow;
                _session.LastError = null;
                _session.LastErrorAt = null;
            }
            else
            {
                _session.Status = "failed";
                _session.LastError = error;
                _session.LastErrorAt = DateTime.UtcNow;
            }
            _session.InFlight = false;
            _session.Plans.Remove(cycleId);
        }
    }

    public void Abort(string cycleId, string error)
    {
        lock (_sync)
        {
            if (_session is null || !_session.Plans.Remove(cycleId))
                throw new KeyNotFoundException("采集计划不存在或已结束。");
            _session.Status = "failed";
            _session.LastError = string.IsNullOrWhiteSpace(error)
                ? "Python 采集端中止了本轮。"
                : error[..Math.Min(1000, error.Length)];
            _session.LastErrorAt = DateTime.UtcNow;
        }
    }

    public PairTrendCollectionStatusResponse GetStatus()
    {
        lock (_sync)
        {
            if (_session is null)
            {
                return new PairTrendCollectionStatusResponse(null, "idle", null, null, null,
                    new Dictionary<string, DateTime>(), 0, 0);
            }

            var bars = _session.Bars.Values.Sum(static byFrequency =>
                byFrequency.Values.Sum(static byEob => (long)byEob.Count));
            var activeCycle = _session.Plans.Values.FirstOrDefault(static item => item.Completed)?.CycleId
                              ?? _session.Plans.Keys.FirstOrDefault();
            return new PairTrendCollectionStatusResponse(_session.TradingDate, _session.Status,
                activeCycle, _session.LastCompletedAt, _session.LastError,
                new Dictionary<string, DateTime>(_session.Watermarks, StringComparer.OrdinalIgnoreCase),
                _session.Bars.Count, bars, _session.LastErrorAt);
        }
    }

    private (Session Session, Cycle Cycle) RequireCycle(string cycleId)
    {
        if (_session is null || string.IsNullOrWhiteSpace(cycleId) ||
            !_session.Plans.TryGetValue(cycleId.Trim(), out var cycle))
            throw new KeyNotFoundException("采集计划不存在或已过期，请重新取得计划。");
        return (_session, cycle);
    }

    private PairTrendCollectionPlanResponse NoPlan(Session session, string reason) => new(
        false, reason, null, session.TradingDate, null, Array.Empty<PairTrendCollectionWindow>(),
        Array.Empty<PairTrendCollectionSymbol>(), session.Universe.Count);

    private static PairTrendCollectionPlanResponse NoPlan(DateOnly tradingDate, string reason) => new(
        false, reason, null, tradingDate, null, Array.Empty<PairTrendCollectionWindow>(),
        Array.Empty<PairTrendCollectionSymbol>(), 0);

    private static void ExpireAbandonedPlans(Session session)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-15);
        var expired = session.Plans.Values
            .Where(item => !item.Completed && item.CreatedAt < cutoff)
            .Select(static item => item.CycleId)
            .ToArray();
        foreach (var cycleId in expired)
        {
            session.Plans.Remove(cycleId);
            session.Status = "failed";
            session.LastError = "采集计划超过 15 分钟未完成，已作废且未推进水位。";
            session.LastErrorAt = DateTime.UtcNow;
        }
    }

    private static DateTime Max(DateTime left, DateTime right) => left >= right ? left : right;
    private static void RequireIdempotentDuplicate(PairTrendBar existing, PairTrendBar candidate)
    {
        var identity = $"{candidate.Symbol}/{candidate.Frequency}/" +
                       $"{candidate.Eob:yyyy-MM-dd HH:mm:ss}";
        if (!string.Equals(existing.SourceRowHash, candidate.SourceRowHash,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{identity} 重复提交但 sourceRowHash 冲突，拒绝覆盖官方 K 线。");
        if (existing != candidate)
            throw new InvalidOperationException(
                $"{identity} 重复提交虽 hash 相同但 OHLCV/时间字段不一致，拒绝幂等伪装。");
    }

    private static string NormalizeSymbol(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("证券代码不能为空。") : value.Trim().ToUpperInvariant();
    private static string NormalizeFrequency(string value) => value.Trim().ToLowerInvariant() switch
    {
        "5m" => "5m", "30m" => "30m", "60m" => "60m", "1d" or "1day" or "day" => "1d",
        _ => throw new ArgumentException($"不支持的 K 线周期: {value}")
    };

    private static HashSet<DateTime> BuildExpectedEobs(
        DateOnly tradingDate,
        PairTrendCollectionWindow window)
    {
        IEnumerable<TimeOnly> closes = window.Frequency.ToLowerInvariant() switch
        {
            "5m" => FiveMinuteCloses(),
            "30m" =>
            [
                new(10, 0), new(10, 30), new(11, 0), new(11, 30),
                new(13, 30), new(14, 0), new(14, 30), new(15, 0)
            ],
            "60m" => [new(10, 30), new(11, 30), new(14, 0), new(15, 0)],
            "1d" => [new(15, 0)],
            _ => throw new InvalidOperationException($"Unsupported frequency: {window.Frequency}")
        };
        var result = closes.Select(tradingDate.ToDateTime)
            .Where(eob => eob > window.From && eob <= window.To)
            .ToHashSet();
        if (result.Count == 0)
            throw new InvalidOperationException(
                $"周期 {window.Frequency} 的计划窗口没有合法闭合 EOB。"
            );
        return result;
    }

    private static IEnumerable<TimeOnly> FiveMinuteCloses()
    {
        for (var value = new TimeOnly(9, 35); value <= new TimeOnly(11, 30); value = value.AddMinutes(5))
            yield return value;
        for (var value = new TimeOnly(13, 5); value <= new TimeOnly(15, 0); value = value.AddMinutes(5))
            yield return value;
    }

    private sealed class Session(DateOnly tradingDate, IReadOnlyList<PairTrendCollectionSymbol> symbols)
    {
        public DateOnly TradingDate { get; } = tradingDate;
        public Dictionary<string, PairTrendCollectionSymbol> Universe { get; } = symbols
            .GroupBy(static item => NormalizeSymbol(item.Symbol), SymbolComparer)
            .ToDictionary(static group => group.Key, static group => group.First() with
            {
                Symbol = group.Key
            }, SymbolComparer);
        public Dictionary<string, Dictionary<string, SortedDictionary<DateTime, PairTrendBar>>> Bars { get; } =
            new(SymbolComparer);
        public Dictionary<string, DateTime> Watermarks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Cycle> Plans { get; } = new(StringComparer.Ordinal);
        public bool InFlight { get; set; }
        public string Status { get; set; } = "idle";
        public DateTime? LastCompletedAt { get; set; }
        public string? LastError { get; set; }
        public DateTime? LastErrorAt { get; set; }
    }

    private sealed class Cycle(
        string cycleId,
        string mode,
        DateOnly tradingDate,
        IReadOnlyList<PairTrendCollectionWindow> windows,
        HashSet<string> expectedSymbols)
    {
        public string CycleId { get; } = cycleId;
        public string Mode { get; } = mode;
        public IReadOnlyList<PairTrendCollectionWindow> Windows { get; } = windows;
        public HashSet<string> ExpectedSymbols { get; } = expectedSymbols;
        public Dictionary<string, HashSet<DateTime>> ExpectedEobsByFrequency { get; } = windows
            .ToDictionary(
                static item => item.Frequency,
                item => BuildExpectedEobs(tradingDate, item),
                StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, HashSet<DateTime>>>
            ReceivedEobsByFrequencyAndSymbol { get; } = windows
                .Select(static item => item.Frequency)
                .ToDictionary(
                    static item => item,
                    _ => new Dictionary<string, HashSet<DateTime>>(SymbolComparer),
                    StringComparer.OrdinalIgnoreCase);
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public bool Completed { get; set; }
    }
}

public sealed record PairTrendCollectionWorkItem(string CycleId, DateOnly TradingDate);

public sealed record PairTrendCollectionSnapshot(
    string CycleId,
    DateOnly TradingDate,
    IReadOnlyList<PairTrendCollectionWindow> Windows,
    IReadOnlyList<PairTrendCollectionSymbolSnapshot> Symbols);

public sealed record PairTrendCollectionSymbolSnapshot(
    string Symbol,
    string? SymbolName,
    IReadOnlyDictionary<string, IReadOnlyList<PairTrendBar>> BarsByFrequency);
