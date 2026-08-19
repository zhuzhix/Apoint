using AStockMonitor.Application.Market;
using AStockMonitor.Application.Recovery;
using AStockMonitor.Application.Collection;
using AStockMonitor.Infrastructure.Market;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace AStockMonitor.Api.Controllers;

/// <summary>行情采集 V4 三通道运行状态与重点 Tick 池查询接口。</summary>
[ApiController]
[Route("api/market-collection-v4")]
[Tags("行情采集V4")]
public sealed class MarketCollectionV4Controller(
    RedisConnectionProvider redisProvider,
    IMySqlConnectionFactory connectionFactory,
    IMarketRecoveryRepository recoveryRepository,
    MarketCollectionV4Options options,
    CollectorControlOptions collectorControlOptions) : ControllerBase
{
    /// <summary>查询快照、重点 Tick、官方 K 线队列的统一状态。</summary>
    /// <remarks>
    /// Snapshot 的 completed_at 和 HotTick 的 updated_at 可直接用于判断进程是否失联；
    /// officialBars 返回最近的官方 K 线主动拉取运行。
    /// </remarks>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Status(CancellationToken cancellationToken)
    {
        var database = (await redisProvider.GetAsync()).GetDatabase();
        var snapshot = ToDictionary(await database.HashGetAllAsync("md:v4:snapshot:status"));
        var hotTick = ToDictionary(await database.HashGetAllAsync("md:v4:hot-tick:status"));
        await using var connection = connectionFactory.Create();
        var desiredCount = await connection.QuerySingleAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM hot_tick_pool_snapshot
            WHERE selected=TRUE AND subscription_date=(
                SELECT MAX(subscription_date) FROM hot_tick_pool_snapshot WHERE selected=TRUE);
            """, cancellationToken: cancellationToken));
        var assignmentRows = (await connection.QueryAsync<TickAssignmentRow>(new CommandDefinition(
            """
            SELECT worker_id WorkerId,assignment_version AssignmentVersion,command_id CommandId,
                   status Status,COALESCE(JSON_LENGTH(symbols),0) SymbolCount,
                   applied_at AppliedAt,last_error LastError,updated_at UpdatedAt
            FROM collector_tick_assignment
            WHERE gateway_id=@GatewayId
            ORDER BY worker_id;
            """, new { GatewayId = collectorControlOptions.GatewayId.Trim() },
            cancellationToken: cancellationToken))).ToArray();
        var assignments = assignmentRows.Select(static row => new
        {
            workerId = row.WorkerId,
            assignmentVersion = row.AssignmentVersion,
            commandId = row.CommandId,
            status = row.Status,
            symbolCount = row.SymbolCount,
            appliedAt = row.AppliedAt,
            lastError = row.LastError,
            updatedAt = row.UpdatedAt
        }).ToArray();
        var appliedCount = assignmentRows.Where(static row =>
            string.Equals(row.Status, "applied", StringComparison.OrdinalIgnoreCase))
            .Sum(static row => row.SymbolCount);

        var runs = await recoveryRepository.QueryRunsAsync(
            1, 50, null, cancellationToken);
        var officialRuns = runs.Items
            .Where(static run => run.TriggerType.StartsWith(
                "official-v4-", StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToArray();
        return Ok(new
        {
            enabled = options.Enabled,
            snapshot = new
            {
                enabled = options.Snapshot.Enabled,
                targetCycleSeconds = options.Snapshot.TargetCycleSeconds,
                staleQuoteSeconds = options.Snapshot.StaleQuoteSeconds,
                state = snapshot
            },
            hotTick = new
            {
                enabled = options.HotTick.Enabled,
                desiredCount,
                appliedCount,
                capacity = options.HotTick.MaxSymbols,
                maxWorkers = options.HotTick.MaxWorkers,
                symbolsPerWorker = options.HotTick.SymbolsPerWorker,
                intradayReserveSymbols = options.HotTick.IntradayReserveSymbols,
                baseRefreshSeconds = options.HotTick.BaseRefreshSeconds,
                state = hotTick,
                assignments
            },
            officialBars = new
            {
                enabled = options.OfficialBars.Enabled,
                maxWorkers = options.OfficialBars.MaxWorkers,
                symbolsPerPartition = options.OfficialBars.SymbolsPerPartition,
                recentRuns = officialRuns
            },
            serverTime = DateTimeOffset.UtcNow
        });
    }

    /// <summary>分页查看当前重点 Tick 订阅候选，按优先级从高到低排列。</summary>
    [HttpGet("hot-tick-symbols")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> HotTickSymbols(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        await using var connection = connectionFactory.Create();
        var total = await connection.QuerySingleAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM hot_tick_pool_snapshot
            WHERE selected=TRUE AND subscription_date=(
                SELECT MAX(subscription_date) FROM hot_tick_pool_snapshot WHERE selected=TRUE);
            """, cancellationToken: cancellationToken));
        var start = (page - 1L) * pageSize;
        var rows = total == 0 || start >= total
            ? Array.Empty<HotTickSelectionRow>()
            : (await connection.QueryAsync<HotTickSelectionRow>(new CommandDefinition(
                """
                SELECT s.symbol Symbol,s.subscription_date SubscriptionDate,
                       s.source_trading_date SourceTradingDate,s.pool_version PoolVersion,
                       s.source_type SourceType,s.strongest_stage StrongestStage,
                       s.active_level_count ActiveLevelCount,s.pivot_types PivotTypes,
                       s.nearest_pair_price NearestPairPrice,s.distance_percent DistancePercent,
                       s.latest_hit_at LatestHitAt,s.rank_no RankNo,s.priority Priority,
                       a.worker_id WorkerId,a.status AssignmentStatus,a.applied_at AssignmentAppliedAt
                FROM hot_tick_pool_snapshot s
                LEFT JOIN collector_tick_assignment a
                  ON a.gateway_id=@GatewayId
                 AND JSON_CONTAINS(a.symbols,JSON_QUOTE(s.symbol),'$')
                WHERE s.selected=TRUE AND s.subscription_date=(
                    SELECT MAX(subscription_date) FROM hot_tick_pool_snapshot WHERE selected=TRUE)
                ORDER BY s.rank_no
                LIMIT @PageSize OFFSET @Offset;
                """, new
                {
                    GatewayId = collectorControlOptions.GatewayId.Trim(),
                    PageSize = pageSize,
                    Offset = start
                }, cancellationToken: cancellationToken))).ToArray();
        return Ok(new
        {
            page,
            pageSize,
            total,
            totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize),
            items = rows.Select(static item => new
            {
                symbol = item.Symbol,
                priority = item.Priority,
                subscriptionDate = item.SubscriptionDate,
                sourceTradingDate = item.SourceTradingDate,
                poolVersion = item.PoolVersion,
                sourceType = item.SourceType,
                strongestStage = item.StrongestStage,
                activeLevelCount = item.ActiveLevelCount,
                pivotTypes = item.PivotTypes,
                nearestPairPrice = item.NearestPairPrice,
                distancePercent = item.DistancePercent,
                latestHitAt = item.LatestHitAt,
                rankNo = item.RankNo,
                workerId = item.WorkerId,
                assignmentStatus = item.AssignmentStatus ?? "pending",
                assignmentAppliedAt = item.AssignmentAppliedAt
            })
        });
    }

    private static IReadOnlyDictionary<string, string> ToDictionary(HashEntry[] values) =>
        values.ToDictionary(
            static entry => entry.Name.ToString(),
            static entry => entry.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

    private sealed class HotTickSelectionRow
    {
        public string Symbol { get; init; } = string.Empty;
        public DateTime SubscriptionDate { get; init; }
        public DateTime SourceTradingDate { get; init; }
        public string PoolVersion { get; init; } = string.Empty;
        public string SourceType { get; init; } = string.Empty;
        public string? StrongestStage { get; init; }
        public long ActiveLevelCount { get; init; }
        public string? PivotTypes { get; init; }
        public decimal? NearestPairPrice { get; init; }
        public decimal? DistancePercent { get; init; }
        public DateTime? LatestHitAt { get; init; }
        public long RankNo { get; init; }
        public long Priority { get; init; }
        public string? WorkerId { get; init; }
        public string? AssignmentStatus { get; init; }
        public DateTime? AssignmentAppliedAt { get; init; }
    }

    private sealed class TickAssignmentRow
    {
        public string WorkerId { get; init; } = string.Empty;
        public string AssignmentVersion { get; init; } = string.Empty;
        public Guid CommandId { get; init; }
        public string Status { get; init; } = string.Empty;
        public long SymbolCount { get; init; }
        public DateTime? AppliedAt { get; init; }
        public string? LastError { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
