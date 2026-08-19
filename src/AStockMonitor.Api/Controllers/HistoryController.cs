using Dapper;
using AStockMonitor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

/// <summary>查询历史 K 线数据底座、下载断点和数据质量状态。</summary>
[ApiController]
[Route("api/history")]
[Produces("application/json")]
[Tags("历史数据底座")]
public sealed class HistoryController(IMySqlConnectionFactory connectionFactory) : ControllerBase
{
    /// <summary>查询历史数据底座总量和最近一次任务状态。</summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>数据量、断点、最近下载批次、质量检查和每日流水线状态。</returns>
    /// <response code="200">查询成功。</response>
    [HttpGet("status")]
    [ProducesResponseType(typeof(HistoryStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<HistoryStatusResponse>> GetStatus(
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);

        var counts = await connection.QuerySingleAsync<HistoryCounts>(new CommandDefinition(
            """
            SELECT
                COALESCE(MAX(CASE WHEN dataset_name='instrument_daily_status' THEN row_count END),0) AS UniverseRows,
                COALESCE(MAX(CASE WHEN dataset_name='kline_bar_5m' THEN row_count END),0) AS Bars5m,
                COALESCE(MAX(CASE WHEN dataset_name='kline_bar_30m' THEN row_count END),0) AS Bars30m,
                COALESCE(MAX(CASE WHEN dataset_name='kline_bar_60m' THEN row_count END),0) AS Bars60m,
                COALESCE(MAX(CASE WHEN dataset_name='kline_bar_daily' THEN row_count END),0) AS Bars1d,
                COALESCE(MAX(CASE WHEN dataset_name='pair_pivot_signal' THEN row_count END),0) AS PairSignals,
                COALESCE(MAX(CASE WHEN dataset_name='pair_trend_event' THEN row_count END),0) AS PairTrendEvents,
                COALESCE(MAX(CASE WHEN dataset_name='pair_trend_hit' THEN row_count END),0) AS PairTrendHits,
                COALESCE(MAX(CASE WHEN dataset_name='bar_quality_issue_open' THEN row_count END),0) AS OpenQualityIssues,
                COALESCE(MIN(is_exact),FALSE) AS IsExact,
                MAX(updated_at) AS SnapshotAt
            FROM dataset_stat_snapshot;
            """,
            commandTimeout: 3,
            cancellationToken: cancellationToken));

        var checkpoints = (await connection.QueryAsync<CheckpointStatus>(new CommandDefinition(
            """
            SELECT status AS Status, COUNT(*) AS Count
            FROM bar_ingest_checkpoint
            GROUP BY status
            ORDER BY status;
            """,
            cancellationToken: cancellationToken))).ToArray();

        var latestBatch = await connection.QuerySingleOrDefaultAsync<LatestBatch>(new CommandDefinition(
            """
            SELECT id AS Id, batch_key AS BatchKey, job_type AS JobType,
                   date_from AS DateFrom, date_to AS DateTo, frequencies AS Frequencies,
                   status AS Status, requested_symbols AS RequestedSymbols,
                   completed_symbols AS CompletedSymbols, rows_read AS RowsRead,
                   rows_written AS RowsWritten, rows_filtered AS RowsFiltered,
                   error_count AS ErrorCount, started_at AS StartedAt, finished_at AS FinishedAt
            FROM bar_ingest_batch
            ORDER BY id DESC LIMIT 1;
            """,
            cancellationToken: cancellationToken));

        var latestQuality = await connection.QuerySingleOrDefaultAsync<LatestQualityRun>(new CommandDefinition(
            """
            SELECT id AS Id, run_key AS RunKey, date_from AS DateFrom, date_to AS DateTo,
                   status AS Status, bars_checked AS BarsChecked, issue_count AS IssueCount,
                   started_at AS StartedAt, finished_at AS FinishedAt
            FROM bar_quality_run
            ORDER BY id DESC LIMIT 1;
            """,
            cancellationToken: cancellationToken));

        var latestPipeline = await connection.QuerySingleOrDefaultAsync<LatestPipelineRun>(new CommandDefinition(
            """
            SELECT id AS Id, trading_date AS TradingDate, pipeline_version AS PipelineVersion,
                   status AS Status, current_stage AS CurrentStage,
                   started_at AS StartedAt, finished_at AS FinishedAt,
                   error_message AS ErrorMessage
            FROM daily_pipeline_run
            ORDER BY id DESC LIMIT 1;
            """,
            cancellationToken: cancellationToken));

        return Ok(new HistoryStatusResponse
        {
            Counts = counts,
            Checkpoints = checkpoints,
            LatestBatch = latestBatch,
            LatestQuality = latestQuality,
            LatestPipeline = latestPipeline
        });
    }

    /// <summary>查询尚未关闭的数据质量问题。</summary>
    /// <param name="limit">返回数量，范围 1～1000，默认 100。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>按 error、warning、其他严重度和最新 ID 排序的问题列表。</returns>
    /// <response code="200">查询成功。</response>
    [HttpGet("quality/issues")]
    [ProducesResponseType(typeof(IReadOnlyCollection<QualityIssue>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<QualityIssue>>> GetOpenQualityIssues(
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var issues = await connection.QueryAsync<QualityIssue>(new CommandDefinition(
            """
            SELECT id AS Id, check_type AS CheckType, symbol AS Symbol,
                   frequency AS Frequency, trading_date AS TradingDate, eob AS Eob,
                   severity AS Severity, message AS Message, details AS Details,
                   created_at AS CreatedAt
            FROM bar_quality_issue
            WHERE status='open'
            ORDER BY
                CASE severity WHEN 'error' THEN 0 WHEN 'warning' THEN 1 ELSE 2 END,
                id DESC
            LIMIT @Limit;
            """,
            new { Limit = limit },
            cancellationToken: cancellationToken));
        return Ok(issues.AsList());
    }

    /// <summary>历史数据底座状态响应。</summary>
    public sealed class HistoryStatusResponse
    {
        /// <summary>各类历史数据和质量问题总量。</summary>
        public HistoryCounts Counts { get; init; } = new();

        /// <summary>按状态汇总的下载断点数量。</summary>
        public IReadOnlyCollection<CheckpointStatus> Checkpoints { get; init; } = [];

        /// <summary>最近一次历史下载批次；尚未运行时为空。</summary>
        public LatestBatch? LatestBatch { get; init; }

        /// <summary>最近一次数据质量检查；尚未运行时为空。</summary>
        public LatestQualityRun? LatestQuality { get; init; }

        /// <summary>最近一次每日增量流水线；尚未运行时为空。</summary>
        public LatestPipelineRun? LatestPipeline { get; init; }
    }

    /// <summary>历史数据表和未关闭质量问题总量。</summary>
    public sealed class HistoryCounts
    {
        /// <summary>按交易日保存的股票池状态行数。</summary>
        public long UniverseRows { get; init; }

        /// <summary>5 分钟 K 线数量。</summary>
        public long Bars5m { get; init; }

        /// <summary>官方 30 分钟 K 线数量。</summary>
        public long Bars30m { get; init; }

        /// <summary>官方 60 分钟 K 线数量。</summary>
        public long Bars60m { get; init; }

        /// <summary>日线数量。</summary>
        public long Bars1d { get; init; }

        /// <summary>第一版对子研究记录数量。</summary>
        public long PairSignals { get; init; }

        /// <summary>pair-trend-v3 事件数量。</summary>
        public long PairTrendEvents { get; init; }

        /// <summary>pair-trend-v3 K 线证据数量。</summary>
        public long PairTrendHits { get; init; }

        /// <summary>尚未关闭的数据质量问题数量。</summary>
        public long OpenQualityIssues { get; init; }

        /// <summary>所有计数是否来自最近一次后台精确统计。</summary>
        public bool IsExact { get; init; }

        /// <summary>统计快照的最近更新时间。</summary>
        public DateTime? SnapshotAt { get; init; }
    }

    /// <summary>某一下载断点状态的数量。</summary>
    public sealed class CheckpointStatus
    {
        /// <summary>断点状态。</summary>
        public string Status { get; init; } = string.Empty;

        /// <summary>该状态对应的断点数量。</summary>
        public long Count { get; init; }
    }

    /// <summary>最近一次历史 K 线下载批次。</summary>
    public sealed class LatestBatch
    {
        /// <summary>批次主键。</summary>
        public long Id { get; init; }
        /// <summary>批次幂等键。</summary>
        public string BatchKey { get; init; } = string.Empty;
        /// <summary>任务类型。</summary>
        public string JobType { get; init; } = string.Empty;
        /// <summary>下载开始日期。</summary>
        public DateTime DateFrom { get; init; }
        /// <summary>下载结束日期。</summary>
        public DateTime DateTo { get; init; }
        /// <summary>下载周期列表。</summary>
        public string Frequencies { get; init; } = string.Empty;
        /// <summary>批次状态。</summary>
        public string Status { get; init; } = string.Empty;
        /// <summary>请求股票数量。</summary>
        public int RequestedSymbols { get; init; }
        /// <summary>完成股票数量。</summary>
        public int CompletedSymbols { get; init; }
        /// <summary>从数据源读取的行数。</summary>
        public long RowsRead { get; init; }
        /// <summary>幂等写入的行数。</summary>
        public long RowsWritten { get; init; }
        /// <summary>因口径或质量规则过滤的行数。</summary>
        public long RowsFiltered { get; init; }
        /// <summary>错误数量。</summary>
        public int ErrorCount { get; init; }
        /// <summary>批次开始时间。</summary>
        public DateTime StartedAt { get; init; }
        /// <summary>批次结束时间。</summary>
        public DateTime? FinishedAt { get; init; }
    }

    /// <summary>最近一次历史 K 线数据质量检查。</summary>
    public sealed class LatestQualityRun
    {
        /// <summary>质量检查运行主键。</summary>
        public long Id { get; init; }
        /// <summary>运行幂等键。</summary>
        public string RunKey { get; init; } = string.Empty;
        /// <summary>检查开始日期。</summary>
        public DateTime DateFrom { get; init; }
        /// <summary>检查结束日期。</summary>
        public DateTime DateTo { get; init; }
        /// <summary>运行状态。</summary>
        public string Status { get; init; } = string.Empty;
        /// <summary>检查的 K 线数量。</summary>
        public long BarsChecked { get; init; }
        /// <summary>发现的问题数量。</summary>
        public long IssueCount { get; init; }
        /// <summary>检查开始时间。</summary>
        public DateTime StartedAt { get; init; }
        /// <summary>检查结束时间。</summary>
        public DateTime? FinishedAt { get; init; }
    }

    /// <summary>最近一次每日历史数据增量流水线。</summary>
    public sealed class LatestPipelineRun
    {
        /// <summary>流水线运行主键。</summary>
        public long Id { get; init; }
        /// <summary>目标交易日。</summary>
        public DateTime TradingDate { get; init; }
        /// <summary>流水线版本。</summary>
        public string PipelineVersion { get; init; } = string.Empty;
        /// <summary>运行状态。</summary>
        public string Status { get; init; } = string.Empty;
        /// <summary>当前或最后执行阶段。</summary>
        public string? CurrentStage { get; init; }
        /// <summary>流水线开始时间。</summary>
        public DateTime StartedAt { get; init; }
        /// <summary>流水线结束时间。</summary>
        public DateTime? FinishedAt { get; init; }
        /// <summary>失败原因；成功时为空。</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>一条尚未关闭的历史 K 线数据质量问题。</summary>
    public sealed class QualityIssue
    {
        /// <summary>质量问题主键。</summary>
        public long Id { get; init; }
        /// <summary>检查规则类型，例如 OHLC、缺失或聚合完整性。</summary>
        public string CheckType { get; init; } = string.Empty;
        /// <summary>关联股票代码；运行级问题可为空。</summary>
        public string? Symbol { get; init; }
        /// <summary>关联 K 线周期。</summary>
        public string? Frequency { get; init; }
        /// <summary>关联交易日。</summary>
        public DateTime? TradingDate { get; init; }
        /// <summary>关联 K 线结束时间。</summary>
        public DateTime? Eob { get; init; }
        /// <summary>严重程度：error、warning 或 info。</summary>
        public string Severity { get; init; } = string.Empty;
        /// <summary>可读的问题说明。</summary>
        public string Message { get; init; } = string.Empty;
        /// <summary>源值、期望值等诊断细节 JSON。</summary>
        public string? Details { get; init; }
        /// <summary>问题记录创建时间。</summary>
        public DateTime CreatedAt { get; init; }
    }
}
