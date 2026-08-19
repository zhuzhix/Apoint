using System.Text.Json;
using AStockMonitor.Application.Recovery;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Infrastructure.Recovery;

/// <summary>缺口检测、水位和补数状态机的MySQL实现。</summary>
public sealed class MySqlMarketRecoveryRepository(IMySqlConnectionFactory connectionFactory)
    : IMarketRecoveryRepository
{
    public async Task<IReadOnlyCollection<EligibleInstrumentDay>> GetEligibleInstrumentDaysAsync(
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<string>? symbols,
        CancellationToken cancellationToken)
    {
        var normalized = symbols?.Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim().ToUpperInvariant()).Distinct().ToArray();
        var symbolFilter = normalized is { Length: > 0 } ? " AND symbol IN @Symbols" : string.Empty;
        var sql = $$"""
            SELECT symbol AS Symbol, trading_date AS TradingDate
            FROM instrument_daily_status
            WHERE trading_date BETWEEN @DateFrom AND @DateTo
              AND is_eligible=TRUE AND is_suspended=FALSE
              {{symbolFilter}}
            ORDER BY trading_date, symbol;
            """;
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<EligibleRow>(new CommandDefinition(
            sql,
            new
            {
                DateFrom = from.ToDateTime(TimeOnly.MinValue),
                DateTo = to.ToDateTime(TimeOnly.MinValue),
                Symbols = normalized
            },
            cancellationToken: cancellationToken));
        return rows.Select(static row => new EligibleInstrumentDay(
            row.Symbol,
            DateOnly.FromDateTime(row.TradingDate))).ToArray();
    }

    public async Task<IReadOnlySet<DateTime>> GetExistingBarEndsAsync(
        string symbol,
        DateOnly tradingDate,
        string frequency,
        CancellationToken cancellationToken)
    {
        var historicalSql = frequency switch
        {
            "5m" => "SELECT eob FROM kline_bar_5m WHERE symbol=@Symbol AND trading_date=@TradingDate AND official_confirmed=TRUE AND source_priority>=300",
            "30m" or "60m" => "SELECT eob FROM kline_bar_agg WHERE symbol=@Symbol AND trading_date=@TradingDate AND frequency=@Frequency AND official_confirmed=TRUE AND source_priority>=300",
            // Daily bars from the provider are stored with a midnight eob. Gap identity is the
            // trading date, so expose the canonical 15:00 slot instead of comparing raw eob.
            "1d" => "SELECT TIMESTAMP(trading_date,'15:00:00') AS eob FROM kline_bar_daily WHERE symbol=@Symbol AND trading_date=@TradingDate AND official_confirmed=TRUE AND source_priority>=300",
            _ => throw new ArgumentOutOfRangeException(nameof(frequency))
        };
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<DateTime>(new CommandDefinition(
            historicalSql,
            new
            {
                Symbol = symbol,
                TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue),
                Frequency = frequency
            },
            cancellationToken: cancellationToken));
        return rows.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlySet<DateTime>>> GetExistingBarEndsAsync(
        IReadOnlyCollection<string> symbols,
        DateOnly tradingDate,
        string frequency,
        CancellationToken cancellationToken)
    {
        if (symbols.Count == 0)
        {
            return new Dictionary<string, IReadOnlySet<DateTime>>();
        }

        var historicalSql = frequency switch
        {
            "5m" => "SELECT symbol, eob FROM kline_bar_5m WHERE symbol IN @Symbols AND trading_date=@TradingDate AND official_confirmed=TRUE AND source_priority>=300",
            "30m" or "60m" => "SELECT symbol, eob FROM kline_bar_agg WHERE symbol IN @Symbols AND trading_date=@TradingDate AND frequency=@Frequency AND official_confirmed=TRUE AND source_priority>=300",
            // See the single-symbol overload: daily completeness is keyed by trading_date.
            "1d" => "SELECT symbol, TIMESTAMP(trading_date,'15:00:00') AS eob FROM kline_bar_daily WHERE symbol IN @Symbols AND trading_date=@TradingDate AND official_confirmed=TRUE AND source_priority>=300",
            _ => throw new ArgumentOutOfRangeException(nameof(frequency))
        };
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<ExistingEndRow>(new CommandDefinition(
            historicalSql,
            new
            {
                Symbols = symbols.ToArray(),
                TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue),
                Frequency = frequency
            },
            cancellationToken: cancellationToken));
        var result = symbols.ToDictionary(
            static symbol => symbol,
            static _ => new HashSet<DateTime>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (result.TryGetValue(row.Symbol, out var ends)) ends.Add(row.Eob);
        }
        return result.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlySet<DateTime>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<MarketRecoveryRunRecord> BeginDetectionRunAsync(
        MarketGapDetectionRequest request,
        int requestedSymbols,
        int overlapSeconds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO market_recovery_run
                (run_key, trigger_type, status, date_from, date_to, cutover_time,
                 overlap_seconds, dry_run, requested_symbols, request_json)
            VALUES
                (@RunKey, @TriggerType, 'validating', @DateFrom, @DateTo, CURRENT_TIMESTAMP(6),
                 @OverlapSeconds, @DryRun, @RequestedSymbols, CAST(@RequestJson AS JSON));
            SELECT LAST_INSERT_ID();
            """;
        await using var connection = connectionFactory.Create();
        var id = await connection.QuerySingleAsync<long>(new CommandDefinition(
            sql,
            new
            {
                RunKey = $"gap:{request.DateFrom:yyyyMMdd}:{request.DateTo:yyyyMMdd}:{Guid.NewGuid():N}",
                TriggerType = request.TriggerType.Trim().ToLowerInvariant(),
                DateFrom = request.DateFrom.ToDateTime(TimeOnly.MinValue),
                DateTo = request.DateTo.ToDateTime(TimeOnly.MinValue),
                OverlapSeconds = overlapSeconds,
                request.DryRun,
                RequestedSymbols = requestedSymbols,
                RequestJson = JsonSerializer.Serialize(request)
            },
            cancellationToken: cancellationToken));
        return (await GetRunAsync(id, cancellationToken))!;
    }

    public async Task<IReadOnlyCollection<MarketGapRecord>> SaveDetectedGapsAsync(
        long runId,
        IReadOnlyCollection<DetectedMarketGap> gaps,
        bool createRecoveryItems,
        CancellationToken cancellationToken)
    {
        if (gaps.Count == 0)
        {
            return [];
        }

        const string gapSql = """
            INSERT INTO market_data_gap
                (gap_key, symbol, dataset, frequency, trading_date, gap_start, gap_end,
                 detect_method, status, severity, expected_count, local_count,
                 missing_count, recovery_run_id)
            VALUES
                (@GapKey, @Symbol, @Dataset, @Frequency, @TradingDate, @GapStart, @GapEnd,
                 'expected-slot', 'detected', @Severity, @ExpectedCount, @LocalCount,
                 @MissingCount, @RunId)
            ON DUPLICATE KEY UPDATE
                id=LAST_INSERT_ID(id),
                recovery_run_id=IF(status IN ('completed','verified_no_bar','source_expired'),
                                   recovery_run_id,VALUES(recovery_run_id)),
                expected_count=VALUES(expected_count), local_count=VALUES(local_count),
                missing_count=VALUES(missing_count), detected_at=CURRENT_TIMESTAMP(6),
                status=IF(status IN ('completed','verified_no_bar','source_expired'),
                          status,'detected'),
                last_error=IF(status IN ('completed','verified_no_bar','source_expired'),
                              last_error,NULL),
                completed_at=IF(status IN ('completed','verified_no_bar','source_expired'),
                                completed_at,NULL);
            """;
        const string itemSql = """
            INSERT INTO market_recovery_item
                (recovery_run_id, gap_id, symbol, dataset, frequency,
                 gap_start, gap_end, next_time, status)
            SELECT @RunId, id, symbol, dataset, frequency, gap_start, gap_end,
                   gap_start, 'planned'
            FROM market_data_gap WHERE gap_key=@GapKey
              AND status NOT IN ('completed','verified_no_bar','source_expired')
            ON DUPLICATE KEY UPDATE gap_id=VALUES(gap_id),
                status=IF(market_recovery_item.status='completed',
                          market_recovery_item.status, 'planned'), last_error=NULL;
            UPDATE market_data_gap SET status='planned'
            WHERE gap_key=@GapKey
              AND status NOT IN ('completed','verified_no_bar','source_expired');
            """;
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var gap in gaps)
        {
            var row = new
            {
                gap.GapKey,
                gap.Symbol,
                gap.Dataset,
                gap.Frequency,
                TradingDate = gap.TradingDate.ToDateTime(TimeOnly.MinValue),
                gap.GapStart,
                gap.GapEnd,
                gap.Severity,
                gap.ExpectedCount,
                gap.LocalCount,
                gap.MissingCount,
                RunId = runId
            };
            await connection.ExecuteAsync(new CommandDefinition(
                gapSql, row, transaction, cancellationToken: cancellationToken));
            if (createRecoveryItems)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    itemSql, row, transaction, cancellationToken: cancellationToken));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        var keys = gaps.Select(static item => item.GapKey).ToArray();
        var rows = await connection.QueryAsync<GapRow>(new CommandDefinition(
            GapSelect + " WHERE gap_key IN @Keys AND recovery_run_id=@RunId " +
            "ORDER BY trading_date, symbol, gap_start;",
            new { Keys = keys, RunId = runId },
            cancellationToken: cancellationToken));
        return rows.Select(static row => row.ToRecord()).ToArray();
    }

    public async Task<MarketRecoveryRunRecord> FinishDetectionRunAsync(
        long runId,
        string status,
        long gapCount,
        string? resultJson,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE market_recovery_run
            SET status=@Status, gaps_detected=@GapCount,
                result_json=IF(@ResultJson IS NULL, NULL, CAST(@ResultJson AS JSON)),
                error_message=@Error,
                finished_at=IF(@Status IN ('completed','detected','failed'), CURRENT_TIMESTAMP(6), NULL)
            WHERE id=@RunId;
            """;
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { RunId = runId, Status = status, GapCount = gapCount, ResultJson = resultJson, Error = error?[..Math.Min(2000, error.Length)] },
            cancellationToken: cancellationToken));
        return (await GetRunAsync(runId, cancellationToken))!;
    }

    public async Task<PagedResult<MarketGapRecord>> QueryGapsAsync(
        int page, int pageSize, string? status, string? symbol, string? dataset,
        DateOnly? dateFrom, DateOnly? dateTo, CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        Add(conditions, parameters, "status", "Status", status?.Trim().ToLowerInvariant());
        Add(conditions, parameters, "symbol", "Symbol", symbol?.Trim().ToUpperInvariant());
        Add(conditions, parameters, "dataset", "Dataset", dataset?.Trim().ToLowerInvariant());
        if (dateFrom is not null) { conditions.Add("trading_date>=@DateFrom"); parameters.Add("DateFrom", dateFrom.Value.ToDateTime(TimeOnly.MinValue)); }
        if (dateTo is not null) { conditions.Add("trading_date<=@DateTo"); parameters.Add("DateTo", dateTo.Value.ToDateTime(TimeOnly.MinValue)); }
        var where = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
        parameters.Add("Limit", pageSize);
        parameters.Add("Offset", (page - 1) * pageSize);
        await using var connection = connectionFactory.Create();
        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM market_data_gap{where};", parameters, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<GapRow>(new CommandDefinition(
            GapSelect + where + " ORDER BY detected_at DESC,id DESC LIMIT @Limit OFFSET @Offset;",
            parameters, cancellationToken: cancellationToken));
        return Page(page, pageSize, total, rows.Select(static row => row.ToRecord()).ToArray());
    }

    public async Task<PagedResult<MarketRecoveryRunRecord>> QueryRunsAsync(
        int page, int pageSize, string? status, CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var where = string.IsNullOrWhiteSpace(status) ? string.Empty : " WHERE status=@Status";
        var parameters = new { Status = status?.Trim().ToLowerInvariant(), Limit = pageSize, Offset = (page - 1) * pageSize };
        await using var connection = connectionFactory.Create();
        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM market_recovery_run{where};", parameters, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition(
            RunSelect + where + " ORDER BY id DESC LIMIT @Limit OFFSET @Offset;", parameters, cancellationToken: cancellationToken));
        return Page(page, pageSize, total, rows.Select(static row => row.ToRecord()).ToArray());
    }

    public async Task<MarketRecoveryRunRecord?> GetRunAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            RunSelect + " WHERE id=@Id;", new { Id = id }, cancellationToken: cancellationToken));
        return row?.ToRecord();
    }

    public async Task<MarketRecoveryRunRecord?> GetLatestRunAsync(
        DateOnly tradingDate,
        string triggerType,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            RunSelect + """
             WHERE date_from=@TradingDate AND date_to=@TradingDate
               AND trigger_type=@TriggerType
               AND status NOT IN ('partial','failed','cancelled')
             ORDER BY id DESC LIMIT 1;
            """,
            new
            {
                TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue),
                TriggerType = triggerType
            },
            cancellationToken: cancellationToken));
        return row?.ToRecord();
    }

    public async Task<bool> CancelRunAsync(
        long id,
        string reason,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE market_recovery_item
            SET status='cancelled', lease_owner=NULL, lease_expires_at=NULL,
                last_error=LEFT(@Reason,2000)
            WHERE recovery_run_id=@Id
              AND status IN ('planned','running','retry_waiting','failed');

            UPDATE market_data_gap g
            INNER JOIN market_recovery_item i ON i.gap_id=g.id
            SET g.status='invalidated', g.last_error=LEFT(@Reason,2000),
                g.completed_at=CURRENT_TIMESTAMP(6)
            WHERE i.recovery_run_id=@Id AND g.status<>'completed';

            UPDATE market_recovery_run
            SET status='cancelled', error_message=LEFT(@Reason,2000),
                finished_at=CURRENT_TIMESTAMP(6)
            WHERE id=@Id
              AND status IN ('validating','detected','planned','running','partial','failed');

            INSERT INTO market_operation_audit
                (operation_type,target_type,target_id,requested_by,reason,result)
            SELECT 'cancel','market_recovery_run',CAST(@Id AS CHAR),
                   LEFT(@RequestedBy,128),LEFT(@Reason,2000),
                   IF(status='cancelled','completed','not_changed')
            FROM market_recovery_run WHERE id=@Id;

            SELECT COUNT(*) FROM market_recovery_run
            WHERE id=@Id AND status='cancelled';
            """;
        await using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                Reason = string.IsNullOrWhiteSpace(reason) ? "operator cancelled" : reason.Trim(),
                RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "unknown" : requestedBy.Trim()
            },
            cancellationToken: cancellationToken)) > 0;
    }

    public async Task<bool> RetryRunAsync(long id, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE market_recovery_item
            SET status='planned', lease_owner=NULL, lease_expires_at=NULL, last_error=NULL
            WHERE recovery_run_id=@Id AND status IN ('failed','retry_waiting');
            UPDATE market_recovery_run SET status='planned', error_message=NULL, finished_at=NULL
            WHERE id=@Id AND status IN ('failed','partial');
            SELECT ROW_COUNT();
            """;
        await using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            sql, new { Id = id }, cancellationToken: cancellationToken)) > 0;
    }

    public async Task<RecoveryStrategyReplayWork?> TryClaimStrategyReplayAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            RunSelect + " WHERE status='strategy_recalculating' " +
            "AND trigger_type NOT LIKE 'official-v4-%' " +
            "ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED;",
            transaction: transaction,
            cancellationToken: cancellationToken));
        if (run is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE market_recovery_run SET status='strategy_running' WHERE id=@Id;",
            new { run.Id }, transaction, cancellationToken: cancellationToken));
        var symbols = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT symbol FROM market_recovery_item WHERE recovery_run_id=@Id ORDER BY symbol;",
            new { run.Id }, transaction, cancellationToken: cancellationToken))).ToArray();
        await transaction.CommitAsync(cancellationToken);
        return new RecoveryStrategyReplayWork(
            run.Id,
            DateOnly.FromDateTime(run.DateFrom),
            DateOnly.FromDateTime(run.DateTo),
            symbols);
    }

    public async Task CompleteStrategyReplayAsync(
        long runId,
        long eventsWritten,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE market_recovery_run
            SET status=IF(@Error IS NULL,'completed','partial'),
                strategy_events_recalculated=@EventsWritten,
                error_message=@Error, finished_at=CURRENT_TIMESTAMP(6)
            WHERE id=@RunId;
            """;
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                RunId = runId,
                EventsWritten = eventsWritten,
                Error = error?[..Math.Min(error.Length, 2000)]
            },
            cancellationToken: cancellationToken));
    }

    private const string GapSelect = """
        SELECT id AS Id, gap_key AS GapKey, symbol AS Symbol, dataset AS Dataset,
               frequency AS Frequency, trading_date AS TradingDate, gap_start AS GapStart,
               gap_end AS GapEnd, detect_method AS DetectMethod, status AS Status,
               severity AS Severity, expected_count AS ExpectedCount, local_count AS LocalCount,
               recovered_count AS RecoveredCount, missing_count AS MissingCount,
               tick_recoverable AS TickRecoverable, recovery_source AS RecoverySource,
               recovery_run_id AS RecoveryRunId, retry_count AS RetryCount,
               last_error AS LastError, detected_at AS DetectedAt, completed_at AS CompletedAt
        FROM market_data_gap
        """;

    private const string RunSelect = """
        SELECT id AS Id, run_key AS RunKey, trigger_type AS TriggerType, status AS Status,
               date_from AS DateFrom, date_to AS DateTo, cutover_time AS CutoverTime,
               overlap_seconds AS OverlapSeconds, dry_run AS DryRun,
               requested_symbols AS RequestedSymbols, completed_symbols AS CompletedSymbols,
               failed_symbols AS FailedSymbols, gaps_detected AS GapsDetected,
               bars_downloaded AS BarsDownloaded, bars_inserted AS BarsInserted,
               bars_revised AS BarsRevised, ticks_replayed AS TicksReplayed,
               quality_issue_count AS QualityIssueCount,
               strategy_events_recalculated AS StrategyEventsRecalculated,
               error_message AS ErrorMessage, started_at AS StartedAt, finished_at AS FinishedAt
        FROM market_recovery_run
        """;

    private static void Add(ICollection<string> conditions, DynamicParameters parameters, string column, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        conditions.Add($"{column}=@{name}"); parameters.Add(name, value);
    }

    private static (int, int) NormalizePage(int page, int size) => (Math.Max(1, page), Math.Clamp(size, 1, 200));
    private static PagedResult<T> Page<T>(int page, int size, long total, IReadOnlyCollection<T> items) =>
        new(page, size, total, (int)Math.Ceiling(total / (double)size), items);

    private sealed class EligibleRow { public string Symbol { get; init; } = ""; public DateTime TradingDate { get; init; } }
    private sealed class ExistingEndRow { public string Symbol { get; init; } = ""; public DateTime Eob { get; init; } }
    private sealed class GapRow
    {
        public long Id { get; init; } public string GapKey { get; init; } = ""; public string Symbol { get; init; } = "";
        public string Dataset { get; init; } = ""; public string? Frequency { get; init; } public DateTime TradingDate { get; init; }
        public DateTime GapStart { get; init; } public DateTime GapEnd { get; init; } public string DetectMethod { get; init; } = "";
        public string Status { get; init; } = ""; public string Severity { get; init; } = ""; public int ExpectedCount { get; init; }
        public int LocalCount { get; init; } public int RecoveredCount { get; init; } public int MissingCount { get; init; }
        public bool? TickRecoverable { get; init; } public string? RecoverySource { get; init; } public long? RecoveryRunId { get; init; }
        public int RetryCount { get; init; } public string? LastError { get; init; } public DateTime DetectedAt { get; init; } public DateTime? CompletedAt { get; init; }
        public MarketGapRecord ToRecord() => new(Id, GapKey, Symbol, Dataset, Frequency, DateOnly.FromDateTime(TradingDate), GapStart, GapEnd, DetectMethod, Status, Severity, ExpectedCount, LocalCount, RecoveredCount, MissingCount, TickRecoverable, RecoverySource, RecoveryRunId, RetryCount, LastError, DetectedAt, CompletedAt);
    }
    private sealed class RunRow
    {
        public long Id { get; init; } public string RunKey { get; init; } = ""; public string TriggerType { get; init; } = ""; public string Status { get; init; } = "";
        public DateTime DateFrom { get; init; } public DateTime DateTo { get; init; } public DateTime? CutoverTime { get; init; } public int OverlapSeconds { get; init; }
        public bool DryRun { get; init; } public int RequestedSymbols { get; init; } public int CompletedSymbols { get; init; } public int FailedSymbols { get; init; }
        public long GapsDetected { get; init; } public long BarsDownloaded { get; init; } public long BarsInserted { get; init; } public long BarsRevised { get; init; }
        public long TicksReplayed { get; init; } public long QualityIssueCount { get; init; } public long StrategyEventsRecalculated { get; init; }
        public string? ErrorMessage { get; init; } public DateTime StartedAt { get; init; } public DateTime? FinishedAt { get; init; }
        public MarketRecoveryRunRecord ToRecord() => new(Id, RunKey, TriggerType, Status, DateOnly.FromDateTime(DateFrom), DateOnly.FromDateTime(DateTo), CutoverTime, OverlapSeconds, DryRun, RequestedSymbols, CompletedSymbols, FailedSymbols, GapsDetected, BarsDownloaded, BarsInserted, BarsRevised, TicksReplayed, QualityIssueCount, StrategyEventsRecalculated, ErrorMessage, StartedAt, FinishedAt);
    }
}
