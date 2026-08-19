using AStockMonitor.Infrastructure.Persistence;
using AStockMonitor.Api.Models;
using AStockMonitor.Application.Market;
using AStockMonitor.Infrastructure.Configuration;
using AStockMonitor.Infrastructure.Market;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Application.Collection;
using AStockMonitor.Domain.Analytics;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace AStockMonitor.Api.Controllers;

/// <summary>查询盘中对子顶底实时事件、完整命中和分片处理状态。</summary>
[ApiController]
[Route("api/pair-trends/live")]
[Produces("application/json")]
[Tags("盘中对子顶底")]
public sealed class PairTrendLiveController(
    IMySqlConnectionFactory connectionFactory,
    RedisConnectionProvider redis,
    MarketOptions marketOptions,
    PairTrendQueryOptions queryOptions,
    IAuthoritativeUniverseRepository authoritativeUniverseRepository) : ControllerBase
{
    /// <summary>分页查询长期实时对子事件。</summary>
    [HttpGet("events")]
    [ProducesResponseType(typeof(PageResponse<LiveEventDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PageResponse<LiveEventDto>>> GetEvents(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? symbol = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? pivotType = null,
        [FromQuery] string? status = null,
        [FromQuery] string? stage = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] string? frequency = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        [FromQuery] bool visibleOnly = false,
        CancellationToken cancellationToken = default)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        Add(conditions, parameters, "symbol", "Symbol", symbol?.Trim().ToUpperInvariant());
        AddStockKeyword(conditions, parameters, "symbol", "symbol_name", keyword);
        Add(conditions, parameters, "pivot_type", "PivotType", pivotType?.Trim().ToUpperInvariant());
        Add(conditions, parameters, "status", "Status", status?.Trim().ToUpperInvariant());
        Add(conditions, parameters, "stage", "Stage", stage?.Trim().ToUpperInvariant());
        Add(conditions, parameters, "is_active", "IsActive", isActive);
        if (!string.IsNullOrWhiteSpace(frequency))
        {
            conditions.Add("FIND_IN_SET(@Frequency,frequencies)>0");
            parameters.Add("Frequency", frequency.Trim().ToLowerInvariant());
        }
        if (dateFrom is not null)
        {
            conditions.Add("last_seen_at>=@DateFrom");
            parameters.Add("DateFrom", dateFrom);
        }
        if (dateTo is not null)
        {
            conditions.Add("last_seen_at<@DateTo");
            parameters.Add("DateTo", dateTo.Value.Date.AddDays(1));
        }
        if (visibleOnly)
        {
            conditions.Add("stage NOT IN ('DISCOVERED','INVALIDATED')");
        }
        var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
        parameters.Add("Offset", (long)(page - 1) * pageSize);
        parameters.Add("PageSize", pageSize);
        await using var connection = connectionFactory.Create();
        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM pair_trend_live_event {where};", parameters,
            cancellationToken: cancellationToken));
        var items = (await connection.QueryAsync<LiveEventDto>(new CommandDefinition(
            $$"""
            SELECT id Id,event_key EventKey,symbol Symbol,symbol_name SymbolName,
                   pivot_type PivotType,status Status,first_seen_at FirstSeenAt,
                   last_seen_at LastSeenAt,confirmed_at ConfirmedAt,
                   latest_pair_price LatestPairPrice,price_ticks PriceTicks,
                   latest_pair_code LatestPairCode,
                   latest_pair_kind LatestPairKind,timeframe_mask TimeframeMask,
                   frequencies Frequencies,strongest_frequency StrongestFrequency,
                   confluence_count ConfluenceCount,total_hit_count TotalHitCount,
                   confirmed_hit_count ConfirmedHitCount,
                   invalidated_hit_count InvalidatedHitCount,
                   pending_hit_count PendingHitCount,retracted_hit_count RetractedHitCount,
                   round_00_hit_count Round00HitCount,
                   double_digit_hit_count DoubleDigitHitCount,score Score,
                   max_trend_strength MaxTrendStrength,algorithm_version AlgorithmVersion,
                   stage Stage,generation Generation,is_active IsActive,
                   discovered_at DiscoveredAt,observed_at ObservedAt,focused_at FocusedAt,
                   established_at EstablishedAt,invalidated_at InvalidatedAt,
                   invalidated_price InvalidatedPrice,invalidation_reason InvalidationReason,
                   root_5m_bob RootFiveMinuteBob,root_5m_eob RootFiveMinuteEob,
                   last_transition_at LastTransitionAt,
                   event_revision EventRevision,last_source_event_id LastSourceEventId,
                   summary_json SummaryJson,created_at CreatedAt,updated_at UpdatedAt
            FROM pair_trend_live_event {{where}}
            ORDER BY last_seen_at DESC,id DESC LIMIT @PageSize OFFSET @Offset;
            """, parameters, cancellationToken: cancellationToken))).ToArray();
        return Ok(Page(page, pageSize, total, items));
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

    /// <summary>查询单个盘中对子事件及其完整 K 线命中明细。</summary>
    /// <param name="id">实时事件主键 ID。</param>
    /// <param name="hitPage">命中明细页码，从 1 开始。</param>
    /// <param name="hitPageSize">每页命中数，范围 1～200。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <response code="200">返回实时事件、命中明细、来源信息和推荐 K 线窗口。</response>
    /// <response code="404">指定实时事件不存在。</response>
    [HttpGet("events/{id:long}")]
    [ProducesResponseType(typeof(LiveEventDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LiveEventDetailResponse>> GetEvent(
        long id,
        [FromQuery] int hitPage = 1,
        [FromQuery] int hitPageSize = 100,
        CancellationToken cancellationToken = default)
    {
        return await GetEventCore(id, hitPage, hitPageSize, null, cancellationToken);
    }

    /// <summary>查询上海交易日当天正式 V3 事件详情；不允许回退到历史日期。</summary>
    [HttpGet("/api/pair-trends/intraday/events/{id:long}")]
    [ProducesResponseType(typeof(LiveEventDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LiveEventDetailResponse>> GetIntradayEvent(
        long id,
        [FromQuery] int hitPage = 1,
        [FromQuery] int hitPageSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (!queryOptions.IntradayEnabled)
            return Conflict(new { code = "PAIR_TREND_INTRADAY_DISABLED" });
        var today = ChinaMarketSession.TradingDate(DateTimeOffset.UtcNow);
        var universe = await authoritativeUniverseRepository.GetStatusAsync(today, cancellationToken);
        if (universe is not { IsReady: true, IsTradingDay: true })
            return NotFound();
        return await GetEventCore(id, hitPage, hitPageSize, today, cancellationToken);
    }

    private async Task<ActionResult<LiveEventDetailResponse>> GetEventCore(
        long id,
        int hitPage,
        int hitPageSize,
        DateOnly? strictTradingDate,
        CancellationToken cancellationToken)
    {
        (hitPage, hitPageSize) = NormalizePage(hitPage, hitPageSize);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SET TRANSACTION READ ONLY;", cancellationToken: cancellationToken));
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        if (strictTradingDate is not null)
        {
            var invalidRoot = await connection.QuerySingleAsync<bool>(new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM pair_trend_live_event
                    WHERE algorithm_version=@AlgorithmVersion AND root_5m_eob IS NULL LIMIT 1);
                """,
                new { AlgorithmVersion = PairTrendOptions.CurrentAlgorithmVersion },
                transaction,
                cancellationToken: cancellationToken));
            if (invalidRoot)
            {
                await transaction.CommitAsync(cancellationToken);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { code = "PAIR_TREND_V3_ROOT_MISSING" });
            }
        }
        var eventWhere = " WHERE id=@Id";
        var eventParameters = new DynamicParameters(new { Id = id });
        if (strictTradingDate is { } date)
        {
            eventWhere += " AND algorithm_version=@AlgorithmVersion" +
                          " AND root_5m_eob>=@DateFrom AND root_5m_eob<@DateToExclusive";
            eventParameters.Add("AlgorithmVersion", PairTrendOptions.CurrentAlgorithmVersion);
            eventParameters.Add("DateFrom", date.ToDateTime(TimeOnly.MinValue));
            eventParameters.Add("DateToExclusive", date.AddDays(1).ToDateTime(TimeOnly.MinValue));
        }
        var pairEvent = await connection.QuerySingleOrDefaultAsync<LiveEventDto>(new CommandDefinition(
            LiveEventSelectSql + eventWhere + ";",
            eventParameters,
            transaction,
            cancellationToken: cancellationToken));
        if (pairEvent is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return NotFound();
        }

        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM pair_trend_live_hit WHERE event_id=@Id;",
            new { Id = id }, transaction, cancellationToken: cancellationToken));
        var hits = (await connection.QueryAsync<LiveHitDto>(new CommandDefinition(
            LiveHitSelectSql +
            " WHERE event_id=@Id ORDER BY observed_at,frequency,id LIMIT @PageSize OFFSET @Offset;",
            new { Id = id, PageSize = hitPageSize, Offset = (long)(hitPage - 1) * hitPageSize },
            transaction,
            cancellationToken: cancellationToken))).ToArray();
        var lifecycles = (await connection.QueryAsync<LiveLifecycleDto>(new CommandDefinition(
            """
            SELECT id Id,lifecycle_key LifecycleKey,from_stage FromStage,to_stage ToStage,
                   occurred_at OccurredAt,trigger_frequency TriggerFrequency,
                   trigger_price TriggerPrice,reason Reason,source_row_hash SourceRowHash,
                   should_notify ShouldNotify,created_at CreatedAt
            FROM pair_trend_live_lifecycle
            WHERE event_id=@Id ORDER BY occurred_at,id;
            """, new { Id = id }, transaction, cancellationToken: cancellationToken))).ToArray();
        await transaction.CommitAsync(cancellationToken);

        return Ok(new LiveEventDetailResponse
        {
            PairEvent = pairEvent,
            Hits = Page(hitPage, hitPageSize, total, hits),
            Lifecycles = lifecycles,
            RecommendedChart = PairTrendRecommendedChart.Create(
                pairEvent.StrongestFrequency, pairEvent.FirstSeenAt, pairEvent.LastSeenAt)
        });
    }

    /// <summary>分页查询盘中对子 K 线命中，包含撤回记录。</summary>
    [HttpGet("hits")]
    [ProducesResponseType(typeof(PageResponse<LiveHitDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PageResponse<LiveHitDto>>> GetHits(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] long? eventId = null,
        [FromQuery] string? symbol = null,
        [FromQuery] string? frequency = null,
        [FromQuery] string? pivotType = null,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        Add(conditions, parameters, "event_id", "EventId", eventId);
        Add(conditions, parameters, "symbol", "Symbol", symbol?.Trim().ToUpperInvariant());
        Add(conditions, parameters, "frequency", "Frequency", frequency?.Trim().ToLowerInvariant());
        Add(conditions, parameters, "pivot_type", "PivotType", pivotType?.Trim().ToUpperInvariant());
        Add(conditions, parameters, "status", "Status", status?.Trim().ToUpperInvariant());
        var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
        parameters.Add("Offset", (long)(page - 1) * pageSize);
        parameters.Add("PageSize", pageSize);
        await using var connection = connectionFactory.Create();
        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM pair_trend_live_hit {where};", parameters,
            cancellationToken: cancellationToken));
        var items = (await connection.QueryAsync<LiveHitDto>(new CommandDefinition(
            $$"""
            SELECT id Id,event_id EventId,hit_key HitKey,symbol Symbol,frequency Frequency,
                   trading_date TradingDate,bob Bob,eob Eob,observed_at ObservedAt,
                   confirmed_at ConfirmedAt,pivot_type PivotType,status Status,
                   pair_price PairPrice,price_ticks PriceTicks,pair_code PairCode,
                   pair_kind PairKind,hit_field HitField,trend_direction TrendDirection,
                   trend_strength TrendStrength,ema20 Ema20,ema60 Ema60,atr14 Atr14,
                   previous_close PreviousClose,open_price OpenPrice,high_price HighPrice,
                   low_price LowPrice,close_price ClosePrice,volume Volume,amount Amount,
                   is_rolling_extreme IsRollingExtreme,volume_percentile VolumePercentile,
                   wick_ratio WickRatio,reversal_atr ReversalAtr,score Score,
                   confirmation_reason ConfirmationReason,source_revision SourceRevision,
                   source_row_hash SourceRowHash,source_event_id SourceEventId,
                   algorithm_version AlgorithmVersion,stage Stage,
                   is_promotion IsPromotion,details_json DetailsJson,
                   created_at CreatedAt,updated_at UpdatedAt
            FROM pair_trend_live_hit {{where}}
            ORDER BY observed_at DESC,id DESC LIMIT @PageSize OFFSET @Offset;
            """, parameters, cancellationToken: cancellationToken))).ToArray();
        return Ok(Page(page, pageSize, total, items));
    }

    /// <summary>查询16个实时对子消费分片的最近成功时间和失败计数。</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(IReadOnlyCollection<ConsumerCheckpointDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<ConsumerCheckpointDto>>> GetStatus(
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        var rows = (await connection.QueryAsync<ConsumerCheckpointDto>(new CommandDefinition(
            """
            SELECT shard Shard,stream_key StreamKey,last_message_id LastMessageId,
                   last_source_event_id LastSourceEventId,last_success_at LastSuccessAt,
                   processed_count ProcessedCount,failure_count FailureCount,
                   last_error LastError,updated_at UpdatedAt
            FROM pair_trend_consumer_checkpoint ORDER BY shard;
            """, cancellationToken: cancellationToken))).ToArray();
        return Ok(rows);
    }

    /// <summary>查询当天 64 个 Tick 失效消费分片的消费者、Pending 和水位。</summary>
    [HttpGet("status/ticks")]
    [ProducesResponseType(typeof(IReadOnlyCollection<TickConsumerStatusDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<TickConsumerStatusDto>>> GetTickStatus(
        CancellationToken cancellationToken = default)
    {
        var tradingDate = ChinaMarketSession.TradingDate(DateTimeOffset.UtcNow);
        var database = (await redis.GetAsync()).GetDatabase();
        var tasks = Enumerable.Range(0, Math.Clamp(marketOptions.TickV3ShardCount, 1, 256))
            .Select(async shard =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = marketOptions.GetV3TickStreamKey(tradingDate, shard);
                if (!await database.KeyExistsAsync(key))
                    return new TickConsumerStatusDto(shard, key, false, 0, 0, null);
                var groups = await database.StreamGroupInfoAsync(key);
                var group = groups.FirstOrDefault(item =>
                    item.Name == "pair-trend-tick-v3");
                return new TickConsumerStatusDto(
                    shard, key, !string.IsNullOrWhiteSpace(group.Name),
                    group.ConsumerCount, group.PendingMessageCount,
                    group.LastDeliveredId?.ToString());
            });
        return Ok((await Task.WhenAll(tasks)).OrderBy(static item => item.Shard).ToArray());
    }

    private static void Add<T>(
        ICollection<string> conditions, DynamicParameters parameters,
        string column, string name, T? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
            return;
        conditions.Add($"{column}=@{name}");
        parameters.Add(name, value);
    }

    private static (int Page, int Size) NormalizePage(int page, int size) =>
        (Math.Max(1, page), Math.Clamp(size, 1, 200));

    private static PageResponse<T> Page<T>(int page, int size, long total, IReadOnlyCollection<T> items) =>
        new(page, size, total, total == 0 ? 0 : (long)Math.Ceiling((decimal)total / size), items);

    private const string LiveEventSelectSql = """
        SELECT id Id,event_key EventKey,symbol Symbol,symbol_name SymbolName,
               pivot_type PivotType,status Status,first_seen_at FirstSeenAt,
               last_seen_at LastSeenAt,confirmed_at ConfirmedAt,
               latest_pair_price LatestPairPrice,price_ticks PriceTicks,
               latest_pair_code LatestPairCode,
               latest_pair_kind LatestPairKind,timeframe_mask TimeframeMask,
               frequencies Frequencies,strongest_frequency StrongestFrequency,
               confluence_count ConfluenceCount,total_hit_count TotalHitCount,
               confirmed_hit_count ConfirmedHitCount,
               invalidated_hit_count InvalidatedHitCount,
               pending_hit_count PendingHitCount,retracted_hit_count RetractedHitCount,
               round_00_hit_count Round00HitCount,double_digit_hit_count DoubleDigitHitCount,
               score Score,max_trend_strength MaxTrendStrength,
               algorithm_version AlgorithmVersion,stage Stage,generation Generation,
               is_active IsActive,discovered_at DiscoveredAt,observed_at ObservedAt,
               focused_at FocusedAt,established_at EstablishedAt,
               invalidated_at InvalidatedAt,invalidated_price InvalidatedPrice,
               invalidation_reason InvalidationReason,root_5m_bob RootFiveMinuteBob,
               root_5m_eob RootFiveMinuteEob,last_transition_at LastTransitionAt,
               event_revision EventRevision,
               last_source_event_id LastSourceEventId,summary_json SummaryJson,
               created_at CreatedAt,updated_at UpdatedAt
        FROM pair_trend_live_event
        """;

    private const string LiveHitSelectSql = """
        SELECT id Id,event_id EventId,hit_key HitKey,symbol Symbol,frequency Frequency,
               trading_date TradingDate,bob Bob,eob Eob,observed_at ObservedAt,
               confirmed_at ConfirmedAt,pivot_type PivotType,status Status,
               pair_price PairPrice,price_ticks PriceTicks,pair_code PairCode,
               pair_kind PairKind,hit_field HitField,trend_direction TrendDirection,
               trend_strength TrendStrength,ema20 Ema20,ema60 Ema60,atr14 Atr14,
               previous_close PreviousClose,open_price OpenPrice,high_price HighPrice,
               low_price LowPrice,close_price ClosePrice,volume Volume,amount Amount,
               is_rolling_extreme IsRollingExtreme,volume_percentile VolumePercentile,
               wick_ratio WickRatio,reversal_atr ReversalAtr,score Score,
               confirmation_reason ConfirmationReason,source_revision SourceRevision,
               source_row_hash SourceRowHash,source_event_id SourceEventId,
               algorithm_version AlgorithmVersion,stage Stage,
               is_promotion IsPromotion,details_json DetailsJson,
               created_at CreatedAt,updated_at UpdatedAt
        FROM pair_trend_live_hit
        """;

    public sealed record PageResponse<T>(
        int Page, int PageSize, long Total, long TotalPages, IReadOnlyCollection<T> Items);

    /// <summary>单个盘中对子事件详情。</summary>
    public sealed class LiveEventDetailResponse
    {
        /// <summary>固定为 live。</summary>
        public string Source { get; init; } = "live";

        /// <summary>实时东方掘金行情来源信息。</summary>
        public PairTrendSourceInfo SourceInfo { get; init; } = new()
        {
            RunMode = "realtime",
            DataSource = "dongcai-gm"
        };

        /// <summary>对子事件汇总。</summary>
        public LiveEventDto PairEvent { get; init; } = new();

        /// <summary>构成事件的 K 线命中明细。</summary>
        public PageResponse<LiveHitDto> Hits { get; init; } =
            new(1, 100, 0, 0, []);

        /// <summary>发现、观察、重点、成立和失效的完整状态变化轨迹。</summary>
        public IReadOnlyCollection<LiveLifecycleDto> Lifecycles { get; init; } = [];

        /// <summary>前端默认展示的 K 线周期和窗口。</summary>
        public PairTrendRecommendedChart RecommendedChart { get; init; } = new();
    }

    public sealed class LiveEventDto
    {
        public long Id { get; init; }
        public string EventKey { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string? SymbolName { get; init; }
        public string PivotType { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public DateTime FirstSeenAt { get; init; }
        public DateTime LastSeenAt { get; init; }
        public DateTime? ConfirmedAt { get; init; }
        public decimal LatestPairPrice { get; init; }
        public long PriceTicks { get; init; }
        public int LatestPairCode { get; init; }
        public string LatestPairKind { get; init; } = string.Empty;
        public int TimeframeMask { get; init; }
        public string Frequencies { get; init; } = string.Empty;
        public string StrongestFrequency { get; init; } = string.Empty;
        public int ConfluenceCount { get; init; }
        public int TotalHitCount { get; init; }
        public int ConfirmedHitCount { get; init; }
        public int InvalidatedHitCount { get; init; }
        public int PendingHitCount { get; init; }
        public int RetractedHitCount { get; init; }
        public int Round00HitCount { get; init; }
        public int DoubleDigitHitCount { get; init; }
        public decimal Score { get; init; }
        public decimal MaxTrendStrength { get; init; }
        public string AlgorithmVersion { get; init; } = string.Empty;
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
        public int EventRevision { get; init; }
        public string LastSourceEventId { get; init; } = string.Empty;
        public string SummaryJson { get; init; } = "{}";
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    public sealed class LiveHitDto
    {
        public long Id { get; init; }
        public long? EventId { get; init; }
        public string HitKey { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string Frequency { get; init; } = string.Empty;
        public DateTime TradingDate { get; init; }
        public DateTime Bob { get; init; }
        public DateTime Eob { get; init; }
        public DateTime ObservedAt { get; init; }
        public DateTime? ConfirmedAt { get; init; }
        public string PivotType { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public decimal PairPrice { get; init; }
        public long PriceTicks { get; init; }
        public int PairCode { get; init; }
        public string PairKind { get; init; } = string.Empty;
        public string HitField { get; init; } = string.Empty;
        public string TrendDirection { get; init; } = string.Empty;
        public decimal TrendStrength { get; init; }
        public decimal Ema20 { get; init; }
        public decimal Ema60 { get; init; }
        public decimal Atr14 { get; init; }
        public decimal? PreviousClose { get; init; }
        public decimal OpenPrice { get; init; }
        public decimal HighPrice { get; init; }
        public decimal LowPrice { get; init; }
        public decimal ClosePrice { get; init; }
        public long Volume { get; init; }
        public decimal Amount { get; init; }
        public bool IsRollingExtreme { get; init; }
        public decimal VolumePercentile { get; init; }
        public decimal WickRatio { get; init; }
        public decimal ReversalAtr { get; init; }
        public decimal Score { get; init; }
        public string? ConfirmationReason { get; init; }
        public int SourceRevision { get; init; }
        public string SourceRowHash { get; init; } = string.Empty;
        public string SourceEventId { get; init; } = string.Empty;
        public string AlgorithmVersion { get; init; } = string.Empty;
        public string Stage { get; init; } = string.Empty;
        public bool IsPromotion { get; init; }
        public string DetailsJson { get; init; } = "{}";
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    public sealed class LiveLifecycleDto
    {
        public long Id { get; init; }
        public string LifecycleKey { get; init; } = string.Empty;
        public string? FromStage { get; init; }
        public string ToStage { get; init; } = string.Empty;
        public DateTime OccurredAt { get; init; }
        public string TriggerFrequency { get; init; } = string.Empty;
        public decimal TriggerPrice { get; init; }
        public string Reason { get; init; } = string.Empty;
        public string SourceRowHash { get; init; } = string.Empty;
        public bool ShouldNotify { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    public sealed class ConsumerCheckpointDto
    {
        public int Shard { get; init; }
        public string StreamKey { get; init; } = string.Empty;
        public string LastMessageId { get; init; } = string.Empty;
        public string LastSourceEventId { get; init; } = string.Empty;
        public DateTime LastSuccessAt { get; init; }
        public long ProcessedCount { get; init; }
        public long FailureCount { get; init; }
        public string? LastError { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    public sealed record TickConsumerStatusDto(
        int Shard,
        string StreamKey,
        bool GroupExists,
        int ConsumerCount,
        long PendingCount,
        string? LastDeliveredId);
}
