using AStockMonitor.Infrastructure.Persistence;
using Dapper;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

/// <summary>查询策略定义、扫描运行、不可变信号和多策略合并机会。</summary>
/// <remarks>接口只提供策略识别和复盘数据，不执行交易、账户或下单操作。</remarks>
[ApiController]
[Route("api/strategies")]
[Produces("application/json")]
[Tags("策略扫描")]
public sealed class StrategiesController(IMySqlConnectionFactory connectionFactory) : ControllerBase
{
    /// <summary>查询当前注册的8个纯价量策略。</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<StrategyDefinitionDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StrategyDefinitionDto>>> GetDefinitions(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT strategy_code StrategyCode, name Name, scan_profile ScanProfile,
                   current_version CurrentVersion, enabled Enabled,
                   CAST(required_frequencies AS CHAR) RequiredFrequenciesJson,
                   updated_at UpdatedAt
            FROM strategy_definition ORDER BY scan_profile, strategy_code;
            """;
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<StrategyDefinitionDto>(new CommandDefinition(
            sql, cancellationToken: cancellationToken));
        return Ok(rows.AsList());
    }

    /// <summary>分页查询不可变策略信号事件。</summary>
    /// <param name="page">页码，从1开始。</param>
    /// <param name="pageSize">每页数量，范围1～200。</param>
    /// <param name="symbol">股票代码，可选。</param>
    /// <param name="strategyCode">策略编码，可选。</param>
    /// <param name="eventType">生命周期事件类型，可选。</param>
    /// <param name="tradingDate">交易日，可选。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpGet("signals")]
    [ProducesResponseType<PagedResponse<StrategySignalDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<StrategySignalDto>>> GetSignals(
        int page = 1, int pageSize = 50, string? symbol = null,
        string? strategyCode = null, string? eventType = null,
        DateOnly? tradingDate = null, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        const string where = """
            WHERE (@Symbol IS NULL OR symbol=@Symbol)
              AND (@StrategyCode IS NULL OR strategy_code=@StrategyCode)
              AND (@EventType IS NULL OR event_type=@EventType)
              AND (@TradingDate IS NULL OR trading_date=@TradingDate)
            """;
        var args = new
        {
            Symbol = Normalize(symbol), StrategyCode = Normalize(strategyCode),
            EventType = Normalize(eventType)?.ToLowerInvariant(),
            TradingDate = tradingDate?.ToDateTime(TimeOnly.MinValue),
            Offset = (page - 1) * pageSize, Limit = pageSize
        };
        await using var connection = connectionFactory.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM strategy_signal_event {where}", args,
            cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<StrategySignalDto>(new CommandDefinition(
            $"""
            SELECT event_id EventId, previous_event_id PreviousEventId, run_id RunId,
                   strategy_code StrategyCode, strategy_version StrategyVersion,
                   symbol Symbol, trading_date TradingDate, observed_at ObservedAt,
                   event_type EventType, action Action, confidence Confidence, score Score,
                   hit_price HitPrice, stop_reference StopReference, target_reference TargetReference,
                   CAST(passed_conditions AS CHAR) PassedConditionsJson,
                   CAST(failed_conditions AS CHAR) FailedConditionsJson,
                   CAST(feature_snapshot AS CHAR) FeatureSnapshotJson,
                   CAST(parameter_snapshot AS CHAR) ParameterSnapshotJson,
                   source_watermark SourceWatermark, created_at CreatedAt
            FROM strategy_signal_event {where}
            ORDER BY observed_at DESC, id DESC LIMIT @Limit OFFSET @Offset;
            """, args, cancellationToken: cancellationToken));
        return Ok(new PagedResponse<StrategySignalDto>(page, pageSize, total, rows.AsList()));
    }

    /// <summary>分页查询同一股票多策略合并后的当日机会。</summary>
    [HttpGet("opportunities")]
    [ProducesResponseType<PagedResponse<StrategyOpportunityDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<StrategyOpportunityDto>>> GetOpportunities(
        int page = 1, int pageSize = 50, DateOnly? tradingDate = null,
        string? symbol = null, string? level = null, string? status = "active",
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        const string where = """
            WHERE (@TradingDate IS NULL OR trading_date=@TradingDate)
              AND (@Symbol IS NULL OR symbol=@Symbol)
              AND (@Level IS NULL OR level=@Level)
              AND (@Status IS NULL OR status=@Status)
            """;
        var args = new
        {
            TradingDate = tradingDate?.ToDateTime(TimeOnly.MinValue),
            Symbol = Normalize(symbol), Level = Normalize(level)?.ToLowerInvariant(),
            Status = Normalize(status)?.ToLowerInvariant(),
            Offset = (page - 1) * pageSize, Limit = pageSize
        };
        await using var connection = connectionFactory.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM strategy_opportunity {where}", args,
            cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<StrategyOpportunityDto>(new CommandDefinition(
            $"""
            SELECT id Id, trading_date TradingDate, symbol Symbol, level Level, status Status,
                   primary_strategy_code PrimaryStrategyCode, highest_score HighestScore,
                   strategy_count StrategyCount, first_seen_at FirstSeenAt, last_seen_at LastSeenAt,
                   weakened_at WeakenedAt, expired_at ExpiredAt, latest_event_id LatestEventId
            FROM strategy_opportunity {where}
            ORDER BY highest_score DESC, last_seen_at DESC, id DESC LIMIT @Limit OFFSET @Offset;
            """, args, cancellationToken: cancellationToken));
        return Ok(new PagedResponse<StrategyOpportunityDto>(page, pageSize, total, rows.AsList()));
    }

    /// <summary>查询一个机会及其全部策略命中明细。</summary>
    [HttpGet("opportunities/{id:long}")]
    [ProducesResponseType<StrategyOpportunityDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StrategyOpportunityDetailDto>> GetOpportunity(
        long id, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        var opportunity = await connection.QuerySingleOrDefaultAsync<StrategyOpportunityDto>(
            new CommandDefinition("""
                SELECT id Id, trading_date TradingDate, symbol Symbol, level Level, status Status,
                       primary_strategy_code PrimaryStrategyCode, highest_score HighestScore,
                       strategy_count StrategyCount, first_seen_at FirstSeenAt, last_seen_at LastSeenAt,
                       weakened_at WeakenedAt, expired_at ExpiredAt, latest_event_id LatestEventId
                FROM strategy_opportunity WHERE id=@Id;
                """, new { Id = id }, cancellationToken: cancellationToken));
        if (opportunity is null) return NotFound();
        var details = await connection.QueryAsync<StrategyOpportunityRuleDto>(new CommandDefinition("""
            SELECT strategy_code StrategyCode, strategy_version StrategyVersion, action Action,
                   confidence Confidence, current_score CurrentScore, highest_score HighestScore,
                   hit_count HitCount, first_seen_at FirstSeenAt, last_seen_at LastSeenAt,
                   latest_event_id LatestEventId, source_watermark SourceWatermark, status Status
            FROM strategy_opportunity_detail WHERE opportunity_id=@Id
            ORDER BY current_score DESC, strategy_code;
            """, new { Id = id }, cancellationToken: cancellationToken));
        return Ok(new StrategyOpportunityDetailDto(opportunity, details.AsList()));
    }

    /// <summary>分页查询策略扫描任务及其运行结果。</summary>
    [HttpGet("scan-runs")]
    [ProducesResponseType<PagedResponse<StrategyScanRunDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<StrategyScanRunDto>>> GetScanRuns(
        int page = 1, int pageSize = 50, string? profile = null, string? status = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        const string where = "WHERE (@Profile IS NULL OR scan_profile=@Profile) AND (@Status IS NULL OR status=@Status)";
        var args = new { Profile = Normalize(profile)?.ToLowerInvariant(), Status = Normalize(status)?.ToLowerInvariant(),
            Offset = (page - 1) * pageSize, Limit = pageSize };
        await using var connection = connectionFactory.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM strategy_scan_run {where}", args, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<StrategyScanRunDto>(new CommandDefinition($"""
            SELECT id Id, run_key RunKey, scan_profile ScanProfile, trigger_type TriggerType,
                   trading_date TradingDate, status Status, requested_symbols RequestedSymbols,
                   completed_symbols CompletedSymbols, qualified_signals QualifiedSignals,
                   error_message ErrorMessage, started_at StartedAt, finished_at FinishedAt
            FROM strategy_scan_run {where}
            ORDER BY started_at DESC, id DESC LIMIT @Limit OFFSET @Offset;
            """, args, cancellationToken: cancellationToken));
        return Ok(new PagedResponse<StrategyScanRunDto>(page, pageSize, total, rows.AsList()));
    }

    /// <summary>分页查询逐时点历史回放任务。</summary>
    [HttpGet("replay-runs")]
    [ProducesResponseType<PagedResponse<StrategyReplayRunDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<StrategyReplayRunDto>>> GetReplayRuns(
        int page = 1, int pageSize = 50, string? status = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        const string where = "WHERE (@Status IS NULL OR status=@Status)";
        var args = new { Status = Normalize(status)?.ToLowerInvariant(),
            Offset = (page - 1) * pageSize, Limit = pageSize };
        await using var connection = connectionFactory.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM strategy_replay_run {where};", args,
            cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<StrategyReplayRunDto>(new CommandDefinition($"""
            SELECT id Id, run_key RunKey, algorithm_version AlgorithmVersion,
                   date_from DateFrom, date_to DateTo, train_end_date TrainEndDate,
                   status Status, requested_symbols RequestedSymbols,
                   completed_symbols CompletedSymbols, evaluated_points EvaluatedPoints,
                   qualified_observations QualifiedObservations, daily_signals DailySignals,
                   error_count ErrorCount, CAST(data_limitations AS CHAR) DataLimitationsJson,
                   started_at StartedAt, finished_at FinishedAt
            FROM strategy_replay_run {where}
            ORDER BY started_at DESC, id DESC LIMIT @Limit OFFSET @Offset;
            """, args, cancellationToken: cancellationToken));
        return Ok(new PagedResponse<StrategyReplayRunDto>(page, pageSize, total, rows.AsList()));
    }

    /// <summary>分页查询回放中各阈值的首次穿越信号及未来收益。</summary>
    [HttpGet("replay-signals")]
    [ProducesResponseType<PagedResponse<StrategyReplaySignalDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<StrategyReplaySignalDto>>> GetReplaySignals(
        long runId, int page = 1, int pageSize = 50, string? strategyCode = null,
        string? symbol = null, decimal? threshold = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        const string where = """
            WHERE s.run_id=@RunId
              AND (@StrategyCode IS NULL OR s.strategy_code=@StrategyCode)
              AND (@Symbol IS NULL OR s.symbol=@Symbol)
              AND (@Threshold IS NULL OR s.threshold_score=@Threshold)
            """;
        var args = new { RunId = runId, StrategyCode = Normalize(strategyCode),
            Symbol = Normalize(symbol), Threshold = threshold,
            Offset = (page - 1) * pageSize, Limit = pageSize };
        await using var connection = connectionFactory.Create();
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM strategy_replay_signal s {where};", args,
            cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<StrategyReplaySignalDto>(new CommandDefinition($"""
            SELECT s.id Id, s.run_id RunId, s.strategy_code StrategyCode,
                   s.strategy_version StrategyVersion, s.symbol Symbol,
                   s.trading_date TradingDate, s.threshold_score Threshold,
                   s.observed_at ObservedAt, s.observed_score ObservedScore,
                   s.action Action, s.confidence Confidence, s.hit_price HitPrice,
                   o.d1_return_pct D1Return, o.d3_return_pct D3Return,
                   o.d5_return_pct D5Return, o.w1_return_pct W1Return,
                   o.mfe5_pct Mfe5, o.mae5_pct Mae5, o.is_complete OutcomeComplete
            FROM strategy_replay_signal s
            LEFT JOIN strategy_replay_outcome o ON o.signal_id=s.id
            {where}
            ORDER BY s.observed_at DESC, s.id DESC LIMIT @Limit OFFSET @Offset;
            """, args, cancellationToken: cancellationToken));
        return Ok(new PagedResponse<StrategyReplaySignalDto>(page, pageSize, total, rows.AsList()));
    }

    /// <summary>查询一次回放的训练集、验证集和全样本阈值校准结果。</summary>
    [HttpGet("replay-runs/{runId:long}/calibrations")]
    [ProducesResponseType<IReadOnlyList<StrategyCalibrationDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StrategyCalibrationDto>>> GetCalibrations(
        long runId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT strategy_code StrategyCode, threshold_score Threshold,
                   sample_segment SampleSegment, sample_count SampleCount,
                   d1_win_rate D1WinRate, d1_avg_return D1Average,
                   d3_win_rate D3WinRate, d3_avg_return D3Average,
                   d5_win_rate D5WinRate, d5_avg_return D5Average,
                   w1_win_rate W1WinRate, w1_avg_return W1Average,
                   mfe5_avg Mfe5Average, mae5_avg Mae5Average,
                   objective_score ObjectiveScore, recommended Recommended,
                   recommendation_reason RecommendationReason
            FROM strategy_calibration_result WHERE run_id=@RunId
            ORDER BY strategy_code, threshold_score,
                     FIELD(sample_segment,'train','validation','all');
            """;
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<StrategyCalibrationDto>(new CommandDefinition(
            sql, new { RunId = runId }, cancellationToken: cancellationToken));
        return Ok(rows.AsList());
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public sealed record PagedResponse<T>(int Page, int PageSize, long Total, IReadOnlyList<T> Items);
    public sealed class StrategyDefinitionDto { public string StrategyCode { get; init; } = ""; public string Name { get; init; } = ""; public string ScanProfile { get; init; } = ""; public string CurrentVersion { get; init; } = ""; public bool Enabled { get; init; } public string RequiredFrequenciesJson { get; init; } = "[]"; public DateTime UpdatedAt { get; init; } }
    public sealed class StrategySignalDto { public string EventId { get; init; } = ""; public string? PreviousEventId { get; init; } public long RunId { get; init; } public string StrategyCode { get; init; } = ""; public string StrategyVersion { get; init; } = ""; public string Symbol { get; init; } = ""; public DateTime TradingDate { get; init; } public DateTime ObservedAt { get; init; } public string EventType { get; init; } = ""; public string Action { get; init; } = ""; public string Confidence { get; init; } = ""; public decimal Score { get; init; } public decimal HitPrice { get; init; } public decimal? StopReference { get; init; } public decimal? TargetReference { get; init; } public string PassedConditionsJson { get; init; } = "[]"; public string FailedConditionsJson { get; init; } = "[]"; public string FeatureSnapshotJson { get; init; } = "{}"; public string ParameterSnapshotJson { get; init; } = "{}"; public string SourceWatermark { get; init; } = ""; public DateTime CreatedAt { get; init; } }
    public sealed class StrategyOpportunityDto { public long Id { get; init; } public DateTime TradingDate { get; init; } public string Symbol { get; init; } = ""; public string Level { get; init; } = ""; public string Status { get; init; } = ""; public string PrimaryStrategyCode { get; init; } = ""; public decimal HighestScore { get; init; } public int StrategyCount { get; init; } public DateTime FirstSeenAt { get; init; } public DateTime LastSeenAt { get; init; } public DateTime? WeakenedAt { get; init; } public DateTime? ExpiredAt { get; init; } public string LatestEventId { get; init; } = ""; }
    public sealed class StrategyOpportunityRuleDto { public string StrategyCode { get; init; } = ""; public string StrategyVersion { get; init; } = ""; public string Action { get; init; } = ""; public string Confidence { get; init; } = ""; public decimal CurrentScore { get; init; } public decimal HighestScore { get; init; } public int HitCount { get; init; } public DateTime FirstSeenAt { get; init; } public DateTime LastSeenAt { get; init; } public string LatestEventId { get; init; } = ""; public string SourceWatermark { get; init; } = ""; public string Status { get; init; } = ""; }
    public sealed record StrategyOpportunityDetailDto(StrategyOpportunityDto Opportunity, IReadOnlyList<StrategyOpportunityRuleDto> Strategies);
    public sealed class StrategyScanRunDto { public long Id { get; init; } public string RunKey { get; init; } = ""; public string ScanProfile { get; init; } = ""; public string TriggerType { get; init; } = ""; public DateTime TradingDate { get; init; } public string Status { get; init; } = ""; public int RequestedSymbols { get; init; } public int CompletedSymbols { get; init; } public int QualifiedSignals { get; init; } public string? ErrorMessage { get; init; } public DateTime StartedAt { get; init; } public DateTime? FinishedAt { get; init; } }
    public sealed class StrategyReplayRunDto { public long Id { get; init; } public string RunKey { get; init; } = ""; public string AlgorithmVersion { get; init; } = ""; public DateTime DateFrom { get; init; } public DateTime DateTo { get; init; } public DateTime? TrainEndDate { get; init; } public string Status { get; init; } = ""; public int RequestedSymbols { get; init; } public int CompletedSymbols { get; init; } public long EvaluatedPoints { get; init; } public long QualifiedObservations { get; init; } public int DailySignals { get; init; } public int ErrorCount { get; init; } public string? DataLimitationsJson { get; init; } public DateTime StartedAt { get; init; } public DateTime? FinishedAt { get; init; } }
    public sealed class StrategyReplaySignalDto { public long Id { get; init; } public long RunId { get; init; } public string StrategyCode { get; init; } = ""; public string StrategyVersion { get; init; } = ""; public string Symbol { get; init; } = ""; public DateTime TradingDate { get; init; } public decimal Threshold { get; init; } public DateTime ObservedAt { get; init; } public decimal ObservedScore { get; init; } public string Action { get; init; } = ""; public string Confidence { get; init; } = ""; public decimal HitPrice { get; init; } public decimal? D1Return { get; init; } public decimal? D3Return { get; init; } public decimal? D5Return { get; init; } public decimal? W1Return { get; init; } public decimal? Mfe5 { get; init; } public decimal? Mae5 { get; init; } public bool? OutcomeComplete { get; init; } }
    public sealed class StrategyCalibrationDto { public string StrategyCode { get; init; } = ""; public decimal Threshold { get; init; } public string SampleSegment { get; init; } = ""; public int SampleCount { get; init; } public decimal? D1WinRate { get; init; } public decimal? D1Average { get; init; } public decimal? D3WinRate { get; init; } public decimal? D3Average { get; init; } public decimal? D5WinRate { get; init; } public decimal? D5Average { get; init; } public decimal? W1WinRate { get; init; } public decimal? W1Average { get; init; } public decimal? Mfe5Average { get; init; } public decimal? Mae5Average { get; init; } public decimal? ObjectiveScore { get; init; } public bool Recommended { get; init; } public string? RecommendationReason { get; init; } }
}
