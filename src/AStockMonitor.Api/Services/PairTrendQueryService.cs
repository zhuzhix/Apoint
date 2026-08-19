using AStockMonitor.Api.Models;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Application.Collection;
using AStockMonitor.Application.Market;
using AStockMonitor.Domain.Analytics;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;
using System.Data;

namespace AStockMonitor.Api.Services;

public sealed class PairTrendQueryService(
    IMySqlConnectionFactory connectionFactory,
    PairTrendQueryOptions options,
    PairTrendQueryCache queryCache,
    PairTrendCollectionSessionStore collectionSessionStore,
    IAuthoritativeUniverseRepository authoritativeUniverseRepository)
{
    public async Task<PairTrendStockGroupPage> GetHistoricalGroupsAsync(
        PairTrendGroupQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireEnabled(options.HistoricalDataEnabled, "PAIR_TREND_HISTORICAL_DATA_DISABLED");
        var today = ChinaMarketSession.TradingDate(now);
        var range = PairTrendQueryPolicy.ResolveRange(query.DateFrom, query.DateTo, today, options);
        return await queryCache.GetOrCreateAsync(
            CreateGroupCacheKey("history", query, range),
            TimeSpan.FromSeconds(options.HistoricalGroupCacheSeconds),
            token => GetGroupsAsync(query, range, range.ToExclusive, token),
            cancellationToken);
    }

    public async Task<PairTrendTimelinePage> GetHistoricalEventsAsync(
        PairTrendEventQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireEnabled(options.HistoricalDataEnabled, "PAIR_TREND_HISTORICAL_DATA_DISABLED");
        var today = ChinaMarketSession.TradingDate(now);
        var range = PairTrendQueryPolicy.ResolveRange(query.DateFrom, query.DateTo, today, options);
        return await GetEventsAsync(query, range, range.ToExclusive, cancellationToken);
    }

    public async Task<PairTrendIntradayStatusResponse> GetIntradayStatusAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireEnabled(options.IntradayEnabled, "PAIR_TREND_INTRADAY_DISABLED");
        var tradingDate = ChinaMarketSession.TradingDate(now);
        await using var connection = connectionFactory.Create();
        await EnsureV3RootIntegrityAsync(connection, null, cancellationToken);
        var universe = await authoritativeUniverseRepository.GetStatusAsync(tradingDate, cancellationToken);
        var day = universe is null
            ? null
            : new PairTrendMarketDayRow(
                universe.IsReady ? "completed" : "inconsistent",
                universe.IsTradingDay,
                universe.SyncedAt);
        var marketDayStatus = PairTrendQueryPolicy.ResolveMarketDayStatus(day);
        var sessionStatus = PairTrendQueryPolicy.ResolveSessionStatus(now, marketDayStatus);
        var collection = collectionSessionStore.GetStatus();
        var isCurrentSession = collection.TradingDate == tradingDate;
        return new PairTrendIntradayStatusResponse(
            tradingDate,
            marketDayStatus == "CALENDAR_PENDING" ? null : day!.IsTradingDay,
            marketDayStatus,
            sessionStatus,
            isCurrentSession ? collection.Status : "not_started",
            isCurrentSession
                ? collection.Watermarks
                : new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase),
            ChinaMarketSession.ToChinaTime(now),
            Latest(ToUtcOffset(day?.LastUpdatedAt),
                ToUtcOffset(isCurrentSession ? collection.LastCompletedAt : null)));
    }

    public async Task<PairTrendStockGroupPage> GetIntradayGroupsAsync(
        PairTrendGroupQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireEnabled(options.IntradayEnabled, "PAIR_TREND_INTRADAY_DISABLED");
        var today = ChinaMarketSession.TradingDate(now);
        if (!await IsConfirmedTradingDayAsync(today, cancellationToken))
            return EmptyGroups(query.Page, query.PageSize);
        var range = PairTrendQueryPolicy.ResolveRange(today, today, today, options);
        var statusAt = ChinaMarketSession.ToChinaTime(now).DateTime.AddTicks(1);
        return await queryCache.GetOrCreateAsync(
            CreateGroupCacheKey("intraday", query, range),
            TimeSpan.FromSeconds(options.IntradayGroupCacheSeconds),
            token => GetGroupsAsync(query, range, statusAt, token),
            cancellationToken);
    }

    public async Task<PairTrendTimelinePage> GetIntradayEventsAsync(
        PairTrendEventQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireEnabled(options.IntradayEnabled, "PAIR_TREND_INTRADAY_DISABLED");
        var today = ChinaMarketSession.TradingDate(now);
        if (!await IsConfirmedTradingDayAsync(today, cancellationToken))
            return EmptyEvents(query.Page, query.PageSize, query.Symbol);
        var range = PairTrendQueryPolicy.ResolveRange(today, today, today, options);
        var statusAt = ChinaMarketSession.ToChinaTime(now).DateTime.AddTicks(1);
        return await GetEventsAsync(query, range, statusAt, cancellationToken);
    }

    private async Task<PairTrendStockGroupPage> GetGroupsAsync(
        PairTrendGroupQuery query,
        PairTrendDateRange range,
        DateTime statusAtExclusive,
        CancellationToken cancellationToken)
    {
        var (page, pageSize) = PairTrendQueryPolicy.NormalizePage(query.Page, query.PageSize, 20);
        ValidateFilters(query);
        var parameters = CreateParameters(query, range, statusAtExclusive);
        parameters.Add("Offset", PairTrendQueryPolicy.CalculateOffset(page, pageSize));
        parameters.Add("PageSize", pageSize);
        var sql = options.UseQueryProjection
            ? BuildStockGroupSqlForAudit(query)
            : BuildCanonicalStockGroupSqlForAudit(query);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SET TRANSACTION READ ONLY;", cancellationToken: cancellationToken));
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await EnsureV3RootIntegrityAsync(connection, transaction, cancellationToken);
        var rows = (await connection.QueryAsync<PairTrendStockGroupQueryRow>(new CommandDefinition(
            sql,
            parameters,
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        var total = rows.Length > 0
            ? rows[0].TotalGroups
            : await connection.QuerySingleAsync<long>(new CommandDefinition(
                options.UseQueryProjection
                    ? BuildStockGroupCountSqlForAudit(query)
                    : BuildCanonicalStockGroupCountSqlForAudit(query),
                CreateParameters(query, range, statusAtExclusive),
                transaction,
                cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        var groups = rows.Select(static row => row.ToDto()).ToArray();
        return new PairTrendStockGroupPage(
            page, pageSize, total, PairTrendQueryPolicy.CalculateTotalPages(total, pageSize), groups);
    }

    private async Task<PairTrendTimelinePage> GetEventsAsync(
        PairTrendEventQuery query,
        PairTrendDateRange range,
        DateTime statusAtExclusive,
        CancellationToken cancellationToken)
    {
        var (page, pageSize) = PairTrendQueryPolicy.NormalizePage(query.Page, query.PageSize, 100);
        ValidateFilters(query);
        var parameters = CreateParameters(query, range, statusAtExclusive);
        parameters.Add("Offset", PairTrendQueryPolicy.CalculateOffset(page, pageSize));
        parameters.Add("PageSize", pageSize);
        var conditions = BuildConditions(query, includeKeyword: true);
        var where = string.Join(" AND ", conditions);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SET TRANSACTION READ ONLY;", cancellationToken: cancellationToken));
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await EnsureV3RootIntegrityAsync(connection, transaction, cancellationToken);
        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM pair_trend_live_event e WHERE {where};",
            parameters,
            transaction,
            cancellationToken: cancellationToken));
        var items = (await connection.QueryAsync<PairTrendTimelineEventDto>(new CommandDefinition(
            $$"""
            {{TimelineSelectSql}}
            WHERE {{where}}
            ORDER BY e.root_5m_eob DESC,e.id DESC
            LIMIT @PageSize OFFSET @Offset;
            """,
            parameters,
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        await transaction.CommitAsync(cancellationToken);
        var symbolName = items.Select(static item => item.SymbolName).FirstOrDefault(static name => name is not null);
        return new PairTrendTimelinePage(
            page, pageSize, total, PairTrendQueryPolicy.CalculateTotalPages(total, pageSize),
            items, query.Symbol, symbolName);
    }

    private async Task<bool> IsConfirmedTradingDayAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var universe = await authoritativeUniverseRepository.GetStatusAsync(date, cancellationToken);
        return universe is { IsReady: true, IsTradingDay: true };
    }

    private static DynamicParameters CreateParameters(
        PairTrendGroupQuery query,
        PairTrendDateRange range,
        DateTime statusAtExclusive)
    {
        var parameters = new DynamicParameters();
        parameters.Add("AlgorithmVersion", PairTrendOptions.CurrentAlgorithmVersion);
        parameters.Add("DateFrom", range.FromInclusive);
        parameters.Add("DateToExclusive", range.ToExclusive);
        parameters.Add("StatusAtExclusive", statusAtExclusive);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
            parameters.Add("Keyword", query.Keyword.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(query.PivotType))
            parameters.Add("PivotType", query.PivotType.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(query.Frequency))
        {
            parameters.Add("Frequency", NormalizeFrequency(query.Frequency));
            parameters.Add("FrequencyMask", FrequencyMask(query.Frequency));
        }
        if (!string.IsNullOrWhiteSpace(query.StageAtEnd))
            parameters.Add("StageAtEnd", query.StageAtEnd.Trim().ToUpperInvariant());
        if (query is PairTrendEventQuery { Symbol: { } symbol } && !string.IsNullOrWhiteSpace(symbol))
            parameters.Add("Symbol", symbol.Trim().ToUpperInvariant());
        return parameters;
    }

    private static string CreateGroupCacheKey(
        string scope,
        PairTrendGroupQuery query,
        PairTrendDateRange range)
    {
        var (page, pageSize) = PairTrendQueryPolicy.NormalizePage(query.Page, query.PageSize, 20);
        return string.Join('|',
            scope,
            range.From.ToString("yyyy-MM-dd"),
            range.To.ToString("yyyy-MM-dd"),
            page,
            pageSize,
            query.Keyword?.Trim().ToUpperInvariant() ?? string.Empty,
            query.PivotType?.Trim().ToUpperInvariant() ?? string.Empty,
            query.Frequency is null ? string.Empty : NormalizeFrequency(query.Frequency),
            query.StageAtEnd?.Trim().ToUpperInvariant() ?? string.Empty,
            query.ActiveAtEnd?.ToString() ?? string.Empty,
            query.IncludeInvalidated);
    }

    private static List<string> BuildConditions(
        PairTrendGroupQuery query,
        bool includeKeyword,
        string alias = "e",
        bool useFrequencyMask = false)
    {
        // root_5m_eob is intentionally strict. Never substitute first_seen_at or last_seen_at.
        var stageAtEnd = StageAtEndSql(alias);
        var conditions = new List<string>
        {
            $"{alias}.algorithm_version=@AlgorithmVersion",
            $"{alias}.root_5m_eob IS NOT NULL",
            $"{alias}.root_5m_eob>=@DateFrom",
            $"{alias}.root_5m_eob<@DateToExclusive"
        };
        if (includeKeyword && !string.IsNullOrWhiteSpace(query.Keyword))
            conditions.Add($"(INSTR(UPPER({alias}.symbol),@Keyword)>0 OR " +
                           $"INSTR(UPPER(COALESCE({alias}.symbol_name,'')),@Keyword)>0)");
        if (!string.IsNullOrWhiteSpace(query.PivotType))
            conditions.Add($"{alias}.pivot_type=@PivotType");
        if (!string.IsNullOrWhiteSpace(query.Frequency))
            conditions.Add(useFrequencyMask
                ? $"({alias}.frequency_mask & @FrequencyMask)<>0"
                : $"FIND_IN_SET(@Frequency,{alias}.frequencies)>0");
        if (!string.IsNullOrWhiteSpace(query.StageAtEnd))
            conditions.Add($"({stageAtEnd})=@StageAtEnd");
        if (query.ActiveAtEnd is not null)
            conditions.Add(query.ActiveAtEnd.Value
                ? $"({stageAtEnd})<>'INVALIDATED'"
                : $"({stageAtEnd})='INVALIDATED'");
        else if (!query.IncludeInvalidated)
            conditions.Add($"({stageAtEnd})<>'INVALIDATED'");
        if (query is PairTrendEventQuery { Symbol: { } symbol } && !string.IsNullOrWhiteSpace(symbol))
            conditions.Add($"{alias}.symbol=@Symbol");
        return conditions;
    }

    private static void ValidateFilters(PairTrendGroupQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.PivotType) &&
            query.PivotType.Trim().ToUpperInvariant() is not ("TOP" or "BOTTOM"))
            throw new ArgumentException("pivotType must be TOP or BOTTOM.");
        if (!string.IsNullOrWhiteSpace(query.StageAtEnd) &&
            query.StageAtEnd.Trim().ToUpperInvariant() is not
                ("DISCOVERED" or "OBSERVING" or "FOCUS" or "ESTABLISHED" or "INVALIDATED"))
            throw new ArgumentException("stageAtEnd is invalid.");
        if (!string.IsNullOrWhiteSpace(query.Frequency) &&
            NormalizeFrequency(query.Frequency) is not ("5m" or "30m" or "60m" or "1d"))
            throw new ArgumentException("frequency is invalid.");
    }

    private static void RequireEnabled(bool enabled, string code)
    {
        if (!enabled) throw new PairTrendQueryDisabledException(code);
    }

    private static PairTrendStockGroupPage EmptyGroups(int page, int pageSize)
    {
        var normalized = PairTrendQueryPolicy.NormalizePage(page, pageSize, 20);
        return new PairTrendStockGroupPage(normalized.Page, normalized.PageSize, 0, 0, []);
    }

    private static PairTrendTimelinePage EmptyEvents(int page, int pageSize, string? symbol)
    {
        var normalized = PairTrendQueryPolicy.NormalizePage(page, pageSize, 100);
        return new PairTrendTimelinePage(normalized.Page, normalized.PageSize, 0, 0, [], symbol);
    }

    private static readonly string TimelineSelectSql = $$"""
        SELECT e.id Id,e.event_key EventKey,e.symbol Symbol,e.symbol_name SymbolName,
               e.root_5m_eob PivotAt,e.pivot_type PivotType,e.latest_pair_price PairPrice,
               e.latest_pair_kind PairKind,e.generation Generation,e.frequencies Frequencies,
               e.strongest_frequency StrongestFrequency,({{StageAtEndSql("e")}}) StageAtEnd,
               (({{StageAtEndSql("e")}})<>'INVALIDATED') IsActiveAtEnd,
               e.stage CurrentStage,e.is_active CurrentIsActive,e.observed_at ObservedAt,
               e.focused_at FocusedAt,e.established_at EstablishedAt,
               e.invalidated_at InvalidatedAt,e.invalidation_reason InvalidationReason,
               e.last_transition_at LastTransitionAt,
               e.wave_calculation_status WaveCalculationStatus,e.wave_signal WaveSignal,
               e.wave_score WaveScore,e.wave_evaluated_at WaveEvaluatedAt,
               e.wave_data_as_of WaveDataAsOf,e.wave_algorithm_version WaveAlgorithmVersion
        FROM pair_trend_live_event e
        """;

    /// <summary>
    /// Returns the exact parameterized SQL used by the stock-group endpoint so deployment
    /// verification can run EXPLAIN ANALYZE against the same query text.
    /// </summary>
    public static string BuildStockGroupSqlForAudit(PairTrendGroupQuery query)
    {
        ValidateFilters(query);
        var conditions = BuildConditions(query, includeKeyword: true, useFrequencyMask: true);
        var where = string.Join(" AND ", conditions);
        var latestConditions = BuildConditions(
            query, includeKeyword: true, alias: "latest_candidate", useFrequencyMask: true);
        latestConditions.Add("latest_candidate.symbol=paged.symbol");
        latestConditions.Add("latest_candidate.root_5m_eob=paged.LatestPivotAt");
        var latestWhere = string.Join(" AND ", latestConditions);
        return $$"""
            WITH filtered AS (
                SELECT e.symbol,e.root_5m_eob,e.pivot_type,
                       ({{StageAtEndSql("e")}}) stage_at_end
                FROM pair_trend_query_event e FORCE INDEX (ix_pair_trend_query_period)
                WHERE {{where}}
            ), grouped AS (
                SELECT symbol,
                   MAX(root_5m_eob) LatestPivotAt,
                   MAX(CASE WHEN pivot_type='TOP' THEN root_5m_eob END) LatestTopAt,
                   MAX(CASE WHEN pivot_type='BOTTOM' THEN root_5m_eob END) LatestBottomAt,
                   COUNT(*) EventCount,SUM(pivot_type='TOP') TopCount,
                   SUM(pivot_type='BOTTOM') BottomCount,
                   SUM(stage_at_end<>'INVALIDATED') ActiveAtEndCount,
                   SUM(stage_at_end='INVALIDATED') InvalidatedAtEndCount
                FROM filtered
                GROUP BY symbol
            ), paged AS (
                SELECT grouped.*,COUNT(*) OVER() TotalGroups
                FROM grouped
                ORDER BY LatestPivotAt DESC,symbol ASC
                LIMIT @PageSize OFFSET @Offset
            )
            SELECT paged.TotalGroups,paged.symbol Symbol,latest.symbol_name SymbolName,
                   paged.LatestPivotAt,paged.LatestTopAt,paged.LatestBottomAt,
                   ({{StageAtEndSql("latest")}}) LatestStageAtEnd,
                   paged.EventCount,paged.TopCount,paged.BottomCount,
                   paged.ActiveAtEndCount,paged.InvalidatedAtEndCount
            FROM paged
            LEFT JOIN LATERAL (
                SELECT latest_candidate.symbol_name,latest_candidate.invalidated_at,
                       latest_candidate.established_at,latest_candidate.focused_at,
                       latest_candidate.observed_at
                FROM pair_trend_query_event latest_candidate
                     FORCE INDEX (ix_pair_trend_query_symbol_period)
                WHERE {{latestWhere}}
                ORDER BY latest_candidate.event_id DESC
                LIMIT 1
            ) latest ON TRUE
            ORDER BY paged.LatestPivotAt DESC,paged.symbol ASC;
            """;
    }

    public static string BuildCanonicalStockGroupSqlForAudit(PairTrendGroupQuery query)
    {
        ValidateFilters(query);
        var conditions = BuildConditions(query, includeKeyword: true);
        var where = string.Join(" AND ", conditions);
        var latestConditions = BuildConditions(query, includeKeyword: true, alias: "latest_candidate");
        latestConditions.Add("latest_candidate.symbol=paged.symbol");
        latestConditions.Add("latest_candidate.root_5m_eob=paged.LatestPivotAt");
        var latestWhere = string.Join(" AND ", latestConditions);
        return $$"""
            WITH filtered AS (
                SELECT e.symbol,e.root_5m_eob,e.pivot_type,
                       ({{StageAtEndSql("e")}}) stage_at_end
                FROM pair_trend_live_event e
                WHERE {{where}}
            ), grouped AS (
                SELECT symbol,
                   MAX(root_5m_eob) LatestPivotAt,
                   MAX(CASE WHEN pivot_type='TOP' THEN root_5m_eob END) LatestTopAt,
                   MAX(CASE WHEN pivot_type='BOTTOM' THEN root_5m_eob END) LatestBottomAt,
                   COUNT(*) EventCount,SUM(pivot_type='TOP') TopCount,
                   SUM(pivot_type='BOTTOM') BottomCount,
                   SUM(stage_at_end<>'INVALIDATED') ActiveAtEndCount,
                   SUM(stage_at_end='INVALIDATED') InvalidatedAtEndCount
                FROM filtered
                GROUP BY symbol
            ), paged AS (
                SELECT grouped.*,COUNT(*) OVER() TotalGroups
                FROM grouped
                ORDER BY LatestPivotAt DESC,symbol ASC
                LIMIT @PageSize OFFSET @Offset
            )
            SELECT paged.TotalGroups,paged.symbol Symbol,latest.symbol_name SymbolName,
                   paged.LatestPivotAt,paged.LatestTopAt,paged.LatestBottomAt,
                   ({{StageAtEndSql("latest")}}) LatestStageAtEnd,
                   paged.EventCount,paged.TopCount,paged.BottomCount,
                   paged.ActiveAtEndCount,paged.InvalidatedAtEndCount
            FROM paged
            LEFT JOIN LATERAL (
                SELECT latest_candidate.symbol_name,latest_candidate.invalidated_at,
                       latest_candidate.established_at,latest_candidate.focused_at,
                       latest_candidate.observed_at
                FROM pair_trend_live_event latest_candidate
                     FORCE INDEX (ix_pair_trend_live_symbol_period)
                WHERE {{latestWhere}}
                ORDER BY latest_candidate.id DESC
                LIMIT 1
            ) latest ON TRUE
            ORDER BY paged.LatestPivotAt DESC,paged.symbol ASC;
            """;
    }

    /// <summary>
    /// Counts filtered stock groups only when the requested page has no rows. Keeping the
    /// fallback in the caller's repeatable-read transaction preserves a stable total for an
    /// out-of-range page without making every normal page consume the grouped CTE twice.
    /// </summary>
    public static string BuildStockGroupCountSqlForAudit(PairTrendGroupQuery query)
    {
        ValidateFilters(query);
        var conditions = BuildConditions(query, includeKeyword: true, useFrequencyMask: true);
        var where = string.Join(" AND ", conditions);
        return $$"""
            SELECT COUNT(*)
            FROM (
                SELECT e.symbol
                FROM pair_trend_query_event e FORCE INDEX (ix_pair_trend_query_period)
                WHERE {{where}}
                GROUP BY e.symbol
            ) grouped_count;
            """;
    }

    public static string BuildCanonicalStockGroupCountSqlForAudit(PairTrendGroupQuery query)
    {
        ValidateFilters(query);
        var conditions = BuildConditions(query, includeKeyword: true);
        var where = string.Join(" AND ", conditions);
        return $$"""
            SELECT COUNT(*)
            FROM (
                SELECT e.symbol
                FROM pair_trend_live_event e
                WHERE {{where}}
                GROUP BY e.symbol
            ) grouped_count;
            """;
    }

    private static string StageAtEndSql(string alias) => $$"""
        CASE
            WHEN {{alias}}.invalidated_at IS NOT NULL AND {{alias}}.invalidated_at<@StatusAtExclusive THEN 'INVALIDATED'
            WHEN {{alias}}.established_at IS NOT NULL AND {{alias}}.established_at<@StatusAtExclusive THEN 'ESTABLISHED'
            WHEN {{alias}}.focused_at IS NOT NULL AND {{alias}}.focused_at<@StatusAtExclusive THEN 'FOCUS'
            WHEN {{alias}}.observed_at IS NOT NULL AND {{alias}}.observed_at<@StatusAtExclusive THEN 'OBSERVING'
            ELSE 'DISCOVERED'
        END
        """;

    private static string NormalizeFrequency(string value) => value.Trim().ToLowerInvariant() switch
    {
        "300s" => "5m",
        "1800s" => "30m",
        "3600s" => "60m",
        "day" => "1d",
        var frequency => frequency
    };

    private static int FrequencyMask(string value) => NormalizeFrequency(value) switch
    {
        "5m" => 1,
        "30m" => 2,
        "60m" => 4,
        "1d" => 8,
        _ => throw new ArgumentException("frequency is invalid.")
    };

    private static async Task EnsureV3RootIntegrityAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var invalid = await connection.QuerySingleAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS(
                SELECT 1 FROM pair_trend_live_event
                WHERE algorithm_version=@AlgorithmVersion AND root_5m_eob IS NULL
                LIMIT 1);
            """,
            new { AlgorithmVersion = PairTrendOptions.CurrentAlgorithmVersion },
            transaction,
            cancellationToken: cancellationToken));
        if (invalid)
            throw new PairTrendDataQualityException("PAIR_TREND_V3_ROOT_MISSING");
    }

    private static DateTimeOffset? ToUtcOffset(DateTime? value) => value is null
        ? null
        : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left >= right ? left : right;

    private sealed class PairTrendStockGroupQueryRow
    {
        public long TotalGroups { get; init; }
        public string? Symbol { get; init; }
        public string? SymbolName { get; init; }
        public DateTime LatestPivotAt { get; init; }
        public DateTime? LatestTopAt { get; init; }
        public DateTime? LatestBottomAt { get; init; }
        public string LatestStageAtEnd { get; init; } = string.Empty;
        public long EventCount { get; init; }
        public long TopCount { get; init; }
        public long BottomCount { get; init; }
        public long ActiveAtEndCount { get; init; }
        public long InvalidatedAtEndCount { get; init; }

        public PairTrendStockGroupDto ToDto() => new()
        {
            Symbol = Symbol!,
            SymbolName = SymbolName,
            LatestPivotAt = LatestPivotAt,
            LatestTopAt = LatestTopAt,
            LatestBottomAt = LatestBottomAt,
            LatestStageAtEnd = LatestStageAtEnd,
            EventCount = EventCount,
            TopCount = TopCount,
            BottomCount = BottomCount,
            ActiveAtEndCount = ActiveAtEndCount,
            InvalidatedAtEndCount = InvalidatedAtEndCount
        };
    }
}

public sealed class PairTrendQueryDisabledException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

public sealed class PairTrendDataQualityException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
