using Dapper;
using AStockMonitor.Infrastructure.Persistence;
using AStockMonitor.Api.Models;
using AStockMonitor.Application.Analytics;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

/// <summary>
/// 查询对子趋势顶底回测运行、归并事件、K 线命中明细和统计结果。
/// </summary>
/// <remarks>
/// 本控制器只读取已经落库的研究记录，不执行交易、不连接账户，也不触发下单。
/// </remarks>
[ApiController]
[Route("api/pair-trends")]
[Produces("application/json")]
[Tags("对子趋势顶底")]
public sealed class PairTrendsController(
    IMySqlConnectionFactory connectionFactory,
    PairTrendQueryOptions queryOptions) : ControllerBase
{
    /// <summary>分页查询对子趋势历史回测运行。</summary>
    /// <param name="page">页码，从 1 开始；小于 1 时按 1 处理。</param>
    /// <param name="pageSize">每页数量，范围 1～200。</param>
    /// <param name="status">运行状态，可选：running、complete、partial。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>回测运行分页结果，包含数据来源和完整算法参数。</returns>
    /// <response code="200">查询成功。</response>
    [HttpGet("runs")]
    [ProducesResponseType(typeof(PagedResponse<BacktestRunDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<BacktestRunDto>>> GetRuns(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (ReplayDisabled() is { } disabled) return disabled;
        (page, pageSize) = NormalizePage(page, pageSize);
        var parameters = new DynamicParameters(new
        {
            Offset = (long)(page - 1) * pageSize,
            PageSize = pageSize
        });
        var where = string.Empty;
        if (!string.IsNullOrWhiteSpace(status))
        {
            where = " WHERE status=@Status";
            parameters.Add("Status", status.Trim().ToLowerInvariant());
        }

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM pair_trend_backtest_run{where};",
            parameters,
            cancellationToken: cancellationToken));
        var items = (await connection.QueryAsync<BacktestRunDto>(new CommandDefinition(
            $$"""
            SELECT id AS Id, run_key AS RunKey, algorithm_version AS AlgorithmVersion,
                   run_mode AS RunMode, data_source AS DataSource, notes AS Notes,
                   date_from AS DateFrom, date_to AS DateTo, frequencies AS Frequencies,
                   parameters_json AS ParametersJson, status AS Status,
                   requested_symbols AS RequestedSymbols,
                   completed_symbols AS CompletedSymbols,
                   failed_symbols AS FailedSymbols, bars_processed AS BarsProcessed,
                   hits_detected AS HitsDetected, events_written AS EventsWritten,
                   error_message AS ErrorMessage, started_at AS StartedAt,
                   finished_at AS FinishedAt, updated_at AS UpdatedAt
            FROM pair_trend_backtest_run
            {{where}}
            ORDER BY id DESC
            LIMIT @PageSize OFFSET @Offset;
            """,
            parameters,
            cancellationToken: cancellationToken))).ToArray();
        return Ok(Page(page, pageSize, total, items));
    }

    /// <summary>分页查询同股票、同顶底方向归并后的对子趋势事件。</summary>
    /// <remarks>
    /// V3 同一事件按精确价格从 5m 发现，依次由 30m、60m、1d 升级。
    /// frequency 使用 FIND_IN_SET 精确筛选周期；ROUND_00 表示 .00。
    /// </remarks>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页数量，范围 1～200。</param>
    /// <param name="runId">回测运行 ID。</param>
    /// <param name="symbol">股票代码，例如 SHSE.600000。</param>
    /// <param name="keyword">股票名称或代码片段，例如 浦发银行、600000。</param>
    /// <param name="pivotType">顶底类型：TOP 或 BOTTOM。</param>
    /// <param name="status">事件状态：CANDIDATE、CONFIRMED 或 INVALIDATED。</param>
    /// <param name="stage">V3阶段：DISCOVERED、OBSERVING、FOCUS、ESTABLISHED 或 INVALIDATED。</param>
    /// <param name="frequency">周期：5m、30m、60m 或 1d。</param>
    /// <param name="pairKind">对子类型：ROUND_00 或 DOUBLE_DIGIT。</param>
    /// <param name="pairCode">对子尾数：0、11、22、33、44、55、66、77、88、99。</param>
    /// <param name="minScore">最低事件评分，范围 0～1。</param>
    /// <param name="dateFrom">最后变化时间下界，包含该时间。</param>
    /// <param name="dateTo">最后变化日期上界，包含该自然日。</param>
    /// <param name="visibleOnly">是否只返回观察、重点、成立阶段；为 true 时隐藏发现和失效。</param>
    /// <param name="sortBy">排序字段：lastSeenAt、firstSeenAt、score、confluence、hits。</param>
    /// <param name="sortDirection">排序方向：asc 或 desc。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>对子趋势事件分页结果。</returns>
    /// <response code="200">查询成功。</response>
    [HttpGet("events")]
    [ProducesResponseType(typeof(PagedResponse<PairEventDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<PairEventDto>>> GetEvents(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] long? runId = null,
        [FromQuery] string? symbol = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? pivotType = null,
        [FromQuery] string? status = null,
        [FromQuery] string? stage = null,
        [FromQuery] string? frequency = null,
        [FromQuery] string? pairKind = null,
        [FromQuery] int? pairCode = null,
        [FromQuery] decimal? minScore = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        [FromQuery] bool visibleOnly = false,
        [FromQuery] string sortBy = "lastSeenAt",
        [FromQuery] string sortDirection = "desc",
        CancellationToken cancellationToken = default)
    {
        if (ReplayDisabled() is { } disabled) return disabled;
        (page, pageSize) = NormalizePage(page, pageSize);
        var (where, parameters) = BuildEventFilter(
            runId, symbol, keyword, pivotType, status, stage, frequency, pairKind,
            pairCode, minScore, dateFrom, dateTo, visibleOnly);
        parameters.Add("Offset", (long)(page - 1) * pageSize);
        parameters.Add("PageSize", pageSize);
        var orderColumn = sortBy.Trim().ToLowerInvariant() switch
        {
            "score" => "e.score",
            "firstseenat" => "e.first_seen_at",
            "confluence" => "e.confluence_count",
            "hits" => "e.total_hit_count",
            _ => "e.last_seen_at"
        };
        var direction = sortDirection.Equals("asc", StringComparison.OrdinalIgnoreCase)
            ? "ASC"
            : "DESC";

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM pair_trend_event e {where};",
            parameters,
            cancellationToken: cancellationToken));
        var items = (await connection.QueryAsync<PairEventDto>(new CommandDefinition(
            $$"""
            {{EventSelectSql}}
            {{where}}
            ORDER BY {{orderColumn}} {{direction}}, e.id {{direction}}
            LIMIT @PageSize OFFSET @Offset;
            """,
            parameters,
            cancellationToken: cancellationToken))).ToArray();
        return Ok(Page(page, pageSize, total, items));
    }

    /// <summary>查询单条对子趋势事件及其 K 线命中明细。</summary>
    /// <param name="id">事件主键 ID。</param>
    /// <param name="hitPage">命中明细页码，从 1 开始。</param>
    /// <param name="hitPageSize">命中明细每页数量，范围 1～200。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>事件汇总以及对应的命中明细分页结果。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="404">指定事件不存在。</response>
    [HttpGet("events/{id:long}")]
    [ProducesResponseType(typeof(PairEventDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PairEventDetailResponse>> GetEvent(
        long id,
        [FromQuery] int hitPage = 1,
        [FromQuery] int hitPageSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (ReplayDisabled() is { } disabled) return disabled;
        (hitPage, hitPageSize) = NormalizePage(hitPage, hitPageSize);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var pairEvent = await connection.QuerySingleOrDefaultAsync<PairEventDto>(new CommandDefinition(
            EventSelectSql + " WHERE e.id=@Id;",
            new { Id = id },
            cancellationToken: cancellationToken));
        if (pairEvent is null)
        {
            return NotFound();
        }

        var totalHits = await connection.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM pair_trend_hit WHERE event_id=@Id;",
            new { Id = id },
            cancellationToken: cancellationToken));
        var hits = (await connection.QueryAsync<PairHitDto>(new CommandDefinition(
            HitSelectSql +
            " WHERE h.event_id=@Id ORDER BY h.observed_at, h.frequency, h.id " +
            "LIMIT @PageSize OFFSET @Offset;",
            new
            {
                Id = id,
                PageSize = hitPageSize,
                Offset = (long)(hitPage - 1) * hitPageSize
            },
            cancellationToken: cancellationToken))).ToArray();
        var lifecycles = (await connection.QueryAsync<PairLifecycleDto>(new CommandDefinition(
            """
            SELECT id Id,event_id EventId,lifecycle_key LifecycleKey,
                   from_stage FromStage,to_stage ToStage,occurred_at OccurredAt,
                   trigger_frequency TriggerFrequency,trigger_price TriggerPrice,
                   reason Reason,source_row_hash SourceRowHash,
                   should_notify ShouldNotify
            FROM pair_trend_lifecycle
            WHERE event_id=@Id ORDER BY occurred_at,id;
            """,
            new { Id = id },
            cancellationToken: cancellationToken))).ToArray();
        var run = await connection.QuerySingleOrDefaultAsync<BacktestSourceDto>(new CommandDefinition(
            """
            SELECT id Id,run_mode RunMode,data_source DataSource,notes Notes
            FROM pair_trend_backtest_run WHERE id=@RunId;
            """,
            new { pairEvent.RunId },
            cancellationToken: cancellationToken));
        var acceptance = pairEvent.Symbol.StartsWith("TEST.", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(run?.RunMode, "acceptance", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(run?.DataSource, "acceptance-fixture", StringComparison.OrdinalIgnoreCase);
        return Ok(new PairEventDetailResponse
        {
            PairEvent = pairEvent,
            Hits = Page(hitPage, hitPageSize, totalHits, hits),
            Lifecycles = lifecycles,
            SourceInfo = new PairTrendSourceInfo
            {
                RunId = pairEvent.RunId,
                RunMode = run?.RunMode ?? "historical",
                DataSource = run?.DataSource ?? "unknown",
                IsAcceptanceSample = acceptance,
                Notes = run?.Notes
            },
            RecommendedChart = PairTrendRecommendedChart.Create(
                pairEvent.StrongestFrequency, pairEvent.FirstSeenAt, pairEvent.LastSeenAt)
        });
    }

    /// <summary>分页查询每根 K 线产生的对子命中明细。</summary>
    /// <remarks>
    /// V3 明细包含发现或升级阶段、是否升级、完整 OHLCV、原因和源行哈希。
    /// 旧指标字段只为数据库兼容保留，V3 判定不使用 EMA、ATR 或趋势阈值。
    /// </remarks>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页数量，范围 1～200。</param>
    /// <param name="runId">回测运行 ID。</param>
    /// <param name="eventId">归并事件 ID。</param>
    /// <param name="symbol">股票代码。</param>
    /// <param name="frequency">周期：5m、30m、60m 或 1d。</param>
    /// <param name="pivotType">顶底类型：TOP 或 BOTTOM。</param>
    /// <param name="status">命中状态：CANDIDATE、CONFIRMED 或 INVALIDATED。</param>
    /// <param name="pairKind">对子类型：ROUND_00 或 DOUBLE_DIGIT。</param>
    /// <param name="pairCode">对子尾数代码。</param>
    /// <param name="dateFrom">发现时间下界，包含该时间。</param>
    /// <param name="dateTo">发现日期上界，包含该自然日。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>K 线命中明细分页结果。</returns>
    /// <response code="200">查询成功。</response>
    [HttpGet("hits")]
    [ProducesResponseType(typeof(PagedResponse<PairHitDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<PairHitDto>>> GetHits(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] long? runId = null,
        [FromQuery] long? eventId = null,
        [FromQuery] string? symbol = null,
        [FromQuery] string? frequency = null,
        [FromQuery] string? pivotType = null,
        [FromQuery] string? status = null,
        [FromQuery] string? pairKind = null,
        [FromQuery] int? pairCode = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken cancellationToken = default)
    {
        if (ReplayDisabled() is { } disabled) return disabled;
        (page, pageSize) = NormalizePage(page, pageSize);
        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        AddEquals(conditions, parameters, "h.run_id", "RunId", runId);
        AddEquals(conditions, parameters, "h.event_id", "EventId", eventId);
        AddEquals(conditions, parameters, "h.symbol", "Symbol", NormalizeSymbol(symbol));
        AddEquals(conditions, parameters, "h.frequency", "Frequency", NormalizeFrequency(frequency));
        AddEquals(conditions, parameters, "h.pivot_type", "PivotType", NormalizeUpper(pivotType));
        AddEquals(conditions, parameters, "h.status", "Status", NormalizeUpper(status));
        AddEquals(conditions, parameters, "h.pair_kind", "PairKind", NormalizePairKind(pairKind));
        AddEquals(conditions, parameters, "h.pair_code", "PairCode", pairCode);
        AddDateRange(conditions, parameters, "h.observed_at", dateFrom, dateTo);
        parameters.Add("PageSize", pageSize);
        parameters.Add("Offset", (long)(page - 1) * pageSize);
        var where = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM pair_trend_hit h{where};",
            parameters,
            cancellationToken: cancellationToken));
        var items = (await connection.QueryAsync<PairHitDto>(new CommandDefinition(
            HitSelectSql + where +
            " ORDER BY h.observed_at DESC, h.id DESC LIMIT @PageSize OFFSET @Offset;",
            parameters,
            cancellationToken: cancellationToken))).ToArray();
        return Ok(Page(page, pageSize, total, items));
    }

    /// <summary>统计对子趋势事件和命中分布。</summary>
    /// <param name="runId">可选回测运行 ID；不传时统计全部运行。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>总量以及按顶底状态、周期、对子类型分组的统计结果。</returns>
    /// <response code="200">统计成功。</response>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(PairStatsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PairStatsResponse>> GetStats(
        [FromQuery] long? runId = null,
        CancellationToken cancellationToken = default)
    {
        if (ReplayDisabled() is { } disabled) return disabled;
        var where = runId is null ? string.Empty : " WHERE run_id=@RunId";
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var totals = await connection.QuerySingleAsync<PairStatsDto>(new CommandDefinition(
            $$"""
            SELECT COUNT(*) AS EventCount,
                   COALESCE(SUM(total_hit_count), 0) AS HitCount,
                   COALESCE(SUM(confirmed_hit_count), 0) AS ConfirmedHitCount,
                   COALESCE(SUM(invalidated_hit_count), 0) AS InvalidatedHitCount,
                   COALESCE(SUM(pending_hit_count), 0) AS PendingHitCount,
                   COALESCE(SUM(round_00_hit_count), 0) AS Round00HitCount,
                   COALESCE(SUM(double_digit_hit_count), 0) AS DoubleDigitHitCount,
                   COUNT(DISTINCT symbol) AS SymbolCount
            FROM pair_trend_event
            {{where}};
            """,
            new { RunId = runId },
            cancellationToken: cancellationToken));
        var byPivotAndStatus = await connection.QueryAsync<GroupCountDto>(new CommandDefinition(
            $"""
            SELECT CONCAT(pivot_type, ':', status) AS Name, COUNT(*) AS Count
            FROM pair_trend_event{where}
            GROUP BY pivot_type, status ORDER BY pivot_type, status;
            """,
            new { RunId = runId },
            cancellationToken: cancellationToken));
        var byFrequency = await connection.QueryAsync<GroupCountDto>(new CommandDefinition(
            $"""
            SELECT frequency AS Name, COUNT(*) AS Count
            FROM pair_trend_hit{where}
            GROUP BY frequency
            ORDER BY FIELD(frequency, '5m', '30m', '60m', '1d');
            """,
            new { RunId = runId },
            cancellationToken: cancellationToken));
        var byPairKind = await connection.QueryAsync<GroupCountDto>(new CommandDefinition(
            $"""
            SELECT pair_kind AS Name, COUNT(*) AS Count
            FROM pair_trend_hit{where}
            GROUP BY pair_kind ORDER BY pair_kind;
            """,
            new { RunId = runId },
            cancellationToken: cancellationToken));
        return Ok(new PairStatsResponse
        {
            Totals = totals,
            ByPivotAndStatus = byPivotAndStatus.ToArray(),
            ByFrequency = byFrequency.ToArray(),
            ByPairKind = byPairKind.ToArray()
        });
    }

    private static (string Where, DynamicParameters Parameters) BuildEventFilter(
        long? runId,
        string? symbol,
        string? keyword,
        string? pivotType,
        string? status,
        string? stage,
        string? frequency,
        string? pairKind,
        int? pairCode,
        decimal? minScore,
        DateTime? dateFrom,
        DateTime? dateTo,
        bool visibleOnly)
    {
        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        AddEquals(conditions, parameters, "e.run_id", "RunId", runId);
        AddEquals(conditions, parameters, "e.symbol", "Symbol", NormalizeSymbol(symbol));
        AddStockKeyword(conditions, parameters, "e.symbol", "e.symbol_name", keyword);
        AddEquals(conditions, parameters, "e.pivot_type", "PivotType", NormalizeUpper(pivotType));
        AddEquals(conditions, parameters, "e.status", "Status", NormalizeUpper(status));
        AddEquals(conditions, parameters, "e.stage", "Stage", NormalizeUpper(stage));
        AddEquals(conditions, parameters, "e.latest_pair_kind", "PairKind", NormalizePairKind(pairKind));
        AddEquals(conditions, parameters, "e.latest_pair_code", "PairCode", pairCode);
        if (!string.IsNullOrWhiteSpace(frequency))
        {
            conditions.Add("FIND_IN_SET(@Frequency, e.frequencies) > 0");
            parameters.Add("Frequency", NormalizeFrequency(frequency));
        }

        if (minScore is not null)
        {
            conditions.Add("e.score >= @MinScore");
            parameters.Add("MinScore", Math.Clamp(minScore.Value, 0m, 1m));
        }

        AddDateRange(conditions, parameters, "e.last_seen_at", dateFrom, dateTo);
        if (visibleOnly)
        {
            conditions.Add("e.stage NOT IN ('DISCOVERED','INVALIDATED')");
        }
        return (
            conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions),
            parameters);
    }

    /// <summary>按股票代码片段或股票名称模糊筛选。</summary>
    private static void AddStockKeyword(
        ICollection<string> conditions,
        DynamicParameters parameters,
        string symbolColumn,
        string symbolNameColumn,
        string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return;
        }

        conditions.Add(
            $"(INSTR({symbolColumn},@StockKeyword)>0 OR " +
            $"INSTR(COALESCE({symbolNameColumn},''),@StockKeyword)>0)");
        parameters.Add("StockKeyword", keyword.Trim().ToUpperInvariant());
    }

    private static void AddDateRange(
        ICollection<string> conditions,
        DynamicParameters parameters,
        string column,
        DateTime? dateFrom,
        DateTime? dateTo)
    {
        if (dateFrom is not null)
        {
            conditions.Add($"{column} >= @DateFrom");
            parameters.Add("DateFrom", dateFrom.Value);
        }

        if (dateTo is not null)
        {
            conditions.Add($"{column} < @DateToExclusive");
            parameters.Add("DateToExclusive", dateTo.Value.Date.AddDays(1));
        }
    }

    private static void AddEquals<T>(
        ICollection<string> conditions,
        DynamicParameters parameters,
        string column,
        string parameterName,
        T? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        conditions.Add($"{column}=@{parameterName}");
        parameters.Add(parameterName, value);
    }

    private static (int Page, int PageSize) NormalizePage(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));

    private ObjectResult? ReplayDisabled() => queryOptions.HistoricalReplayEnabled
        ? null
        : StatusCode(StatusCodes.Status409Conflict, new
        {
            code = "PAIR_TREND_BACKTEST_DISABLED",
            message = "Historical pair-trend replay is disabled."
        });

    private static PagedResponse<T> Page<T>(
        int page,
        int pageSize,
        long total,
        IReadOnlyCollection<T> items) => new()
        {
            Page = page,
            PageSize = pageSize,
            Total = total,
            TotalPages = total == 0 ? 0 : (long)Math.Ceiling((decimal)total / pageSize),
            Items = items
        };

    private static string? NormalizeUpper(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string? NormalizeSymbol(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string? NormalizeFrequency(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "300s" => "5m",
        "1800s" => "30m",
        "3600s" => "60m",
        "day" => "1d",
        { } frequency => frequency,
        _ => null
    };

    private static string? NormalizePairKind(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "00" or "ROUND00" or "ROUND_00" => "ROUND_00",
        "PAIR" or "DOUBLE" or "DOUBLE_DIGIT" => "DOUBLE_DIGIT",
        { } kind => kind,
        _ => null
    };

    private const string EventSelectSql = """
        SELECT e.id AS Id, e.run_id AS RunId, e.event_key AS EventKey,
               e.symbol AS Symbol, e.symbol_name AS SymbolName,
               e.pivot_type AS PivotType, e.status AS Status,
               e.first_seen_at AS FirstSeenAt, e.last_seen_at AS LastSeenAt,
               e.confirmed_at AS ConfirmedAt,
               e.latest_pair_price AS LatestPairPrice,
               e.price_ticks AS PriceTicks,
               e.latest_pair_code AS LatestPairCode,
               e.latest_pair_kind AS LatestPairKind,
               e.timeframe_mask AS TimeframeMask, e.frequencies AS Frequencies,
               e.strongest_frequency AS StrongestFrequency,
               e.confluence_count AS ConfluenceCount,
               e.total_hit_count AS TotalHitCount,
               e.confirmed_hit_count AS ConfirmedHitCount,
               e.invalidated_hit_count AS InvalidatedHitCount,
               e.pending_hit_count AS PendingHitCount,
               e.round_00_hit_count AS Round00HitCount,
               e.double_digit_hit_count AS DoubleDigitHitCount,
               e.score AS Score, e.max_trend_strength AS MaxTrendStrength,
               e.algorithm_version AS AlgorithmVersion,
               e.stage AS Stage,e.generation AS Generation,e.is_active AS IsActive,
               e.discovered_at AS DiscoveredAt,e.observed_at AS ObservedAt,
               e.focused_at AS FocusedAt,e.established_at AS EstablishedAt,
               e.invalidated_at AS InvalidatedAt,
               e.invalidated_price AS InvalidatedPrice,
               e.invalidation_reason AS InvalidationReason,
               e.root_5m_bob AS RootFiveMinuteBob,e.root_5m_eob AS RootFiveMinuteEob,
               e.last_transition_at AS LastTransitionAt,
               e.summary_json AS SummaryJson,
               e.created_at AS CreatedAt, e.updated_at AS UpdatedAt
        FROM pair_trend_event e
        """;

    private const string HitSelectSql = """
        SELECT h.id AS Id, h.run_id AS RunId, h.event_id AS EventId,
               h.hit_key AS HitKey, h.symbol AS Symbol, h.frequency AS Frequency,
               h.trading_date AS TradingDate, h.bob AS Bob, h.eob AS Eob,
               h.observed_at AS ObservedAt, h.confirmed_at AS ConfirmedAt,
               h.pivot_type AS PivotType, h.status AS Status,
               h.pair_price AS PairPrice,h.price_ticks AS PriceTicks,
               h.pair_code AS PairCode,
               h.pair_kind AS PairKind, h.hit_field AS HitField,
               h.trend_direction AS TrendDirection,
               h.trend_strength AS TrendStrength, h.ema20 AS Ema20,
               h.ema60 AS Ema60, h.atr14 AS Atr14,
               h.previous_close AS PreviousClose,
               h.open_price AS OpenPrice, h.high_price AS HighPrice,
               h.low_price AS LowPrice, h.close_price AS ClosePrice,
               h.volume AS Volume, h.amount AS Amount,
               h.is_rolling_extreme AS IsRollingExtreme,
               h.volume_percentile AS VolumePercentile,
               h.wick_ratio AS WickRatio, h.reversal_atr AS ReversalAtr,
               h.score AS Score, h.confirmation_reason AS ConfirmationReason,
               h.source_row_hash AS SourceRowHash,
               h.algorithm_version AS AlgorithmVersion,
               h.stage AS Stage,h.is_promotion AS IsPromotion,
               h.details_json AS DetailsJson,
               h.created_at AS CreatedAt, h.updated_at AS UpdatedAt
        FROM pair_trend_hit h
        """;

    /// <summary>统一分页响应。</summary>
    /// <typeparam name="T">列表元素类型。</typeparam>
    public sealed class PagedResponse<T>
    {
        /// <summary>当前页码，从 1 开始。</summary>
        public int Page { get; init; }

        /// <summary>当前请求的每页数量。</summary>
        public int PageSize { get; init; }

        /// <summary>符合筛选条件的记录总数。</summary>
        public long Total { get; init; }

        /// <summary>总页数；没有记录时为 0。</summary>
        public long TotalPages { get; init; }

        /// <summary>当前页记录。</summary>
        public IReadOnlyCollection<T> Items { get; init; } = [];
    }

    /// <summary>对子趋势事件及其命中明细分页结果。</summary>
    public sealed class PairEventDetailResponse
    {
        /// <summary>固定为 history。</summary>
        public string Source { get; init; } = "history";

        /// <summary>历史回放运行和行情来源信息。</summary>
        public PairTrendSourceInfo SourceInfo { get; init; } = new();

        /// <summary>同股票、同顶底方向、同事件窗口的汇总记录。</summary>
        public PairEventDto PairEvent { get; init; } = new();

        /// <summary>构成该事件的每根 K 线命中明细。</summary>
        public PagedResponse<PairHitDto> Hits { get; init; } = new();

        /// <summary>发现、观察、重点、成立和失效的完整状态时间线。</summary>
        public IReadOnlyCollection<PairLifecycleDto> Lifecycles { get; init; } = [];

        /// <summary>前端默认展示的 K 线周期和窗口。</summary>
        public PairTrendRecommendedChart RecommendedChart { get; init; } = new();
    }

    private sealed class BacktestSourceDto
    {
        public long Id { get; init; }
        public string RunMode { get; init; } = string.Empty;
        public string DataSource { get; init; } = string.Empty;
        public string? Notes { get; init; }
    }

    /// <summary>对子趋势统计响应。</summary>
    public sealed class PairStatsResponse
    {
        /// <summary>事件、命中、确认状态及股票数量总计。</summary>
        public PairStatsDto Totals { get; init; } = new();

        /// <summary>按 TOP/BOTTOM 与事件状态组合分组。</summary>
        public IReadOnlyCollection<GroupCountDto> ByPivotAndStatus { get; init; } = [];

        /// <summary>按 5m、30m、60m、1d 周期分组。</summary>
        public IReadOnlyCollection<GroupCountDto> ByFrequency { get; init; } = [];

        /// <summary>按 ROUND_00、DOUBLE_DIGIT 对子类型分组。</summary>
        public IReadOnlyCollection<GroupCountDto> ByPairKind { get; init; } = [];
    }

    /// <summary>一次可复现的对子趋势回测运行。</summary>
    public sealed class BacktestRunDto
    {
        /// <summary>回测运行主键。</summary>
        public long Id { get; init; }

        /// <summary>由日期、周期、股票范围、来源和参数生成的稳定幂等键。</summary>
        public string RunKey { get; init; } = string.Empty;

        /// <summary>算法版本，例如 pair-trend-v3。</summary>
        public string AlgorithmVersion { get; init; } = string.Empty;

        /// <summary>运行类型：historical 或 acceptance。</summary>
        public string RunMode { get; init; } = string.Empty;

        /// <summary>数据来源，例如 dongcai-gm 或 acceptance-fixture。</summary>
        public string DataSource { get; init; } = string.Empty;

        /// <summary>运行备注；验收样本会明确标记为非真实行情。</summary>
        public string? Notes { get; init; }

        /// <summary>回测开始交易日。</summary>
        public DateTime DateFrom { get; init; }

        /// <summary>回测结束交易日。</summary>
        public DateTime DateTo { get; init; }

        /// <summary>参与回测的周期列表。</summary>
        public string Frequencies { get; init; } = string.Empty;

        /// <summary>用于复现结果的完整算法参数 JSON。</summary>
        public string ParametersJson { get; init; } = "{}";

        /// <summary>运行状态：running、complete 或 partial。</summary>
        public string Status { get; init; } = string.Empty;

        /// <summary>请求处理的股票数量。</summary>
        public int RequestedSymbols { get; init; }

        /// <summary>成功完成的股票数量。</summary>
        public int CompletedSymbols { get; init; }

        /// <summary>处理失败的股票数量。</summary>
        public int FailedSymbols { get; init; }

        /// <summary>回测日期范围内处理的 K 线数量。</summary>
        public long BarsProcessed { get; init; }

        /// <summary>识别出的 K 线对子命中数量。</summary>
        public long HitsDetected { get; init; }

        /// <summary>归并写入的事件数量。</summary>
        public long EventsWritten { get; init; }

        /// <summary>运行级错误信息；成功时为空。</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>运行开始时间。</summary>
        public DateTime StartedAt { get; init; }

        /// <summary>运行结束时间；运行中为空。</summary>
        public DateTime? FinishedAt { get; init; }

        /// <summary>记录最后更新时间。</summary>
        public DateTime UpdatedAt { get; init; }
    }

    /// <summary>同股票、同顶底方向、同事件窗口归并后的对子趋势事件。</summary>
    public sealed class PairEventDto
    {
        /// <summary>事件主键。</summary>
        public long Id { get; init; }

        /// <summary>所属回测运行 ID。</summary>
        public long RunId { get; init; }

        /// <summary>事件业务幂等键。</summary>
        public string EventKey { get; init; } = string.Empty;

        /// <summary>股票代码。</summary>
        public string Symbol { get; init; } = string.Empty;

        /// <summary>回测时保存的股票名称。</summary>
        public string? SymbolName { get; init; }

        /// <summary>顶底类型：TOP 或 BOTTOM。</summary>
        public string PivotType { get; init; } = string.Empty;

        /// <summary>事件状态：CANDIDATE、CONFIRMED 或 INVALIDATED。</summary>
        public string Status { get; init; } = string.Empty;

        /// <summary>该事件第一次产生候选的 K 线结束时间。</summary>
        public DateTime FirstSeenAt { get; init; }

        /// <summary>该事件最后一次命中的 K 线结束时间。</summary>
        public DateTime LastSeenAt { get; init; }

        /// <summary>最早确认时间；尚未确认时为空。</summary>
        public DateTime? ConfirmedAt { get; init; }

        /// <summary>该事件最新命中的对子价格。</summary>
        public decimal LatestPairPrice { get; init; }

        /// <summary>对子价格转换后的整数 tick，用于完全同价比较。</summary>
        public long PriceTicks { get; init; }

        /// <summary>最新对子尾数代码，.00 为 0。</summary>
        public int LatestPairCode { get; init; }

        /// <summary>最新对子类型：ROUND_00 或 DOUBLE_DIGIT。</summary>
        public string LatestPairKind { get; init; } = string.Empty;

        /// <summary>周期位图：5m=1、30m=2、60m=4、1d=8。</summary>
        public int TimeframeMask { get; init; }

        /// <summary>事件实际包含的周期列表。</summary>
        public string Frequencies { get; init; } = string.Empty;

        /// <summary>事件包含的最高周期。</summary>
        public string StrongestFrequency { get; init; } = string.Empty;

        /// <summary>参与共振的不同周期数量。</summary>
        public int ConfluenceCount { get; init; }

        /// <summary>事件包含的命中总数。</summary>
        public int TotalHitCount { get; init; }

        /// <summary>已确认命中数。</summary>
        public int ConfirmedHitCount { get; init; }

        /// <summary>已失效命中数。</summary>
        public int InvalidatedHitCount { get; init; }

        /// <summary>仍等待后续 K 线确认的命中数。</summary>
        public int PendingHitCount { get; init; }

        /// <summary>.00（ROUND_00）命中数。</summary>
        public int Round00HitCount { get; init; }

        /// <summary>.11～.99（DOUBLE_DIGIT）命中数。</summary>
        public int DoubleDigitHitCount { get; init; }

        /// <summary>综合评分，范围 0～1，包含多周期共振加分。</summary>
        public decimal Score { get; init; }

        /// <summary>事件内最大的 EMA 差/ATR 趋势强度。</summary>
        public decimal MaxTrendStrength { get; init; }

        /// <summary>算法版本。</summary>
        public string AlgorithmVersion { get; init; } = string.Empty;

        /// <summary>V3阶段：DISCOVERED、OBSERVING、FOCUS、ESTABLISHED、INVALIDATED。</summary>
        public string Stage { get; init; } = string.Empty;
        public int Generation { get; init; }
        public bool IsActive { get; init; }
        public DateTime? DiscoveredAt { get; init; }
        public DateTime? ObservedAt { get; init; }
        public DateTime? FocusedAt { get; init; }
        public DateTime? EstablishedAt { get; init; }
        public DateTime? InvalidatedAt { get; init; }
        public decimal? InvalidatedPrice { get; init; }
        public string? InvalidationReason { get; init; }
        public DateTime? RootFiveMinuteBob { get; init; }
        public DateTime? RootFiveMinuteEob { get; init; }
        public DateTime? LastTransitionAt { get; init; }

        /// <summary>用于审计和快速展示的事件摘要 JSON。</summary>
        public string SummaryJson { get; init; } = "{}";

        /// <summary>数据库创建时间。</summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>数据库更新时间。</summary>
        public DateTime UpdatedAt { get; init; }
    }

    /// <summary>单根收盘 K 线产生的对子趋势命中明细。</summary>
    public sealed class PairHitDto
    {
        /// <summary>命中记录主键。</summary>
        public long Id { get; init; }

        /// <summary>所属回测运行 ID。</summary>
        public long RunId { get; init; }

        /// <summary>所属归并事件 ID。</summary>
        public long EventId { get; init; }

        /// <summary>股票、周期、K 线时间、顶底和算法版本生成的幂等键。</summary>
        public string HitKey { get; init; } = string.Empty;

        /// <summary>股票代码。</summary>
        public string Symbol { get; init; } = string.Empty;

        /// <summary>K 线周期：5m、30m、60m 或 1d。</summary>
        public string Frequency { get; init; } = string.Empty;

        /// <summary>交易日。</summary>
        public DateTime TradingDate { get; init; }

        /// <summary>K 线开始时间。</summary>
        public DateTime Bob { get; init; }

        /// <summary>K 线结束时间。</summary>
        public DateTime Eob { get; init; }

        /// <summary>盘中最早可发现候选的时间，当前等于 K 线结束时间。</summary>
        public DateTime ObservedAt { get; init; }

        /// <summary>后续 K 线确认时间；未确认或失效时为空。</summary>
        public DateTime? ConfirmedAt { get; init; }

        /// <summary>顶底类型：TOP 或 BOTTOM。</summary>
        public string PivotType { get; init; } = string.Empty;

        /// <summary>命中状态：CANDIDATE、CONFIRMED 或 INVALIDATED。</summary>
        public string Status { get; init; } = string.Empty;

        /// <summary>命中的对子价格。</summary>
        public decimal PairPrice { get; init; }

        /// <summary>用于跨周期完全同价比较的整数 tick。</summary>
        public long PriceTicks { get; init; }

        /// <summary>对子尾数代码，.00 为 0。</summary>
        public int PairCode { get; init; }

        /// <summary>对子类型：ROUND_00 或 DOUBLE_DIGIT。</summary>
        public string PairKind { get; init; } = string.Empty;

        /// <summary>命中价格字段：顶部使用 HIGH，底部使用 LOW。</summary>
        public string HitField { get; init; } = string.Empty;

        /// <summary>候选出现前已经确认的趋势方向：UP 或 DOWN。</summary>
        public string TrendDirection { get; init; } = string.Empty;

        /// <summary>趋势强度，计算为 EMA20 与 EMA60 差值的 ATR14 倍数。</summary>
        public decimal TrendStrength { get; init; }

        /// <summary>候选 K 线之前的 EMA20。</summary>
        public decimal Ema20 { get; init; }

        /// <summary>候选 K 线之前的 EMA60。</summary>
        public decimal Ema60 { get; init; }

        /// <summary>候选 K 线之前的 Wilder ATR14。</summary>
        public decimal Atr14 { get; init; }

        /// <summary>上一根已完成 K 线收盘价。</summary>
        public decimal? PreviousClose { get; init; }

        /// <summary>候选 K 线开盘价。</summary>
        public decimal OpenPrice { get; init; }

        /// <summary>候选 K 线最高价。</summary>
        public decimal HighPrice { get; init; }

        /// <summary>候选 K 线最低价。</summary>
        public decimal LowPrice { get; init; }

        /// <summary>候选 K 线收盘价。</summary>
        public decimal ClosePrice { get; init; }

        /// <summary>候选 K 线成交量。</summary>
        public long Volume { get; init; }

        /// <summary>候选 K 线成交额。</summary>
        public decimal Amount { get; init; }

        /// <summary>是否为前 20 根 K 线窗口的新高或新低。</summary>
        public bool IsRollingExtreme { get; init; }

        /// <summary>当前成交量在最近 20 根窗口中的百分位，范围 0～1。</summary>
        public decimal VolumePercentile { get; init; }

        /// <summary>顶部上影线或底部下影线占整根 K 线振幅的比例。</summary>
        public decimal WickRatio { get; init; }

        /// <summary>确认窗口内价格离开对子价的最大 ATR 倍数。</summary>
        public decimal ReversalAtr { get; init; }

        /// <summary>周期、趋势、极值、量能、影线和确认状态综合评分。</summary>
        public decimal Score { get; init; }

        /// <summary>确认、失效或等待后续 K 线的机器可读原因。</summary>
        public string? ConfirmationReason { get; init; }

        /// <summary>源 K 线 SHA-256 哈希，用于追溯结果。</summary>
        public string SourceRowHash { get; init; } = string.Empty;

        /// <summary>产生该记录的算法版本。</summary>
        public string AlgorithmVersion { get; init; } = string.Empty;

        /// <summary>该证据完成后的V3阶段。</summary>
        public string Stage { get; init; } = string.Empty;

        /// <summary>是否使事件从一个阶段升级到下一阶段。</summary>
        public bool IsPromotion { get; init; }

        /// <summary>价格 tick、确认窗口和事件窗口等完整细节 JSON。</summary>
        public string DetailsJson { get; init; } = "{}";

        /// <summary>数据库创建时间。</summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>数据库更新时间。</summary>
        public DateTime UpdatedAt { get; init; }
    }

    /// <summary>V3事件生命周期中的一次不可变状态变化。</summary>
    public sealed class PairLifecycleDto
    {
        public long Id { get; init; }
        public long EventId { get; init; }
        public string LifecycleKey { get; init; } = string.Empty;
        public string? FromStage { get; init; }
        public string ToStage { get; init; } = string.Empty;
        public DateTime OccurredAt { get; init; }
        public string TriggerFrequency { get; init; } = string.Empty;
        public decimal TriggerPrice { get; init; }
        public string Reason { get; init; } = string.Empty;
        public string SourceRowHash { get; init; } = string.Empty;
        public bool ShouldNotify { get; init; }
    }

    /// <summary>对子趋势事件和命中总计。</summary>
    public sealed class PairStatsDto
    {
        /// <summary>事件数量。</summary>
        public long EventCount { get; init; }

        /// <summary>事件包含的命中总数。</summary>
        public long HitCount { get; init; }

        /// <summary>已确认命中数。</summary>
        public long ConfirmedHitCount { get; init; }

        /// <summary>已失效命中数。</summary>
        public long InvalidatedHitCount { get; init; }

        /// <summary>待确认命中数。</summary>
        public long PendingHitCount { get; init; }

        /// <summary>.00 命中数。</summary>
        public long Round00HitCount { get; init; }

        /// <summary>.11～.99 命中数。</summary>
        public long DoubleDigitHitCount { get; init; }

        /// <summary>去重股票数量。</summary>
        public long SymbolCount { get; init; }
    }

    /// <summary>名称与数量组成的分组统计项。</summary>
    public sealed class GroupCountDto
    {
        /// <summary>分组名称。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>该分组的记录数量。</summary>
        public long Count { get; init; }
    }
}
