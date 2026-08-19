using System.Collections.Concurrent;
using System.Data;
using AStockMonitor.Api.Models;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Domain.Analytics;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Api.Services;

/// <summary>
/// Durable pull queue plus short-lived in-memory daily-bar sessions. Python only
/// collects; all validation and scoring stays in WebAPI.
/// </summary>
public sealed class WaveBottomCollectionService(
    IMySqlConnectionFactory connectionFactory,
    ILogger<WaveBottomCollectionService> logger)
{
    public const int MaximumSymbolsPerClaim = 200;
    public const int MaximumBarsPerBatch = 2_000;
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);
    private readonly WaveBottomScorer _scorer = new(new WaveBottomOptions());
    private readonly ConcurrentDictionary<string, LeaseSession> _sessions = new(StringComparer.Ordinal);

    public async Task<WaveBottomClaimResponse> ClaimAsync(
        string collectorId,
        int requestedMaximum,
        CancellationToken cancellationToken)
    {
        foreach (var expired in _sessions.Where(static pair =>
                     pair.Value.ExpiresAt < DateTime.UtcNow).Select(static pair => pair.Key))
            _sessions.TryRemove(expired, out _);
        collectorId = collectorId.Trim();
        if (collectorId.Length is < 3 or > 128)
            throw new ArgumentException("collectorId 长度无效。", nameof(collectorId));
        var maximum = Math.Clamp(requestedMaximum, 1, MaximumSymbolsPerClaim);
        var leaseToken = Guid.NewGuid().ToString();

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var rows = (await connection.QueryAsync<JobRow>(new CommandDefinition(
            """
            SELECT job.id JobId,job.event_id EventId,job.symbol Symbol,
                   job.focused_at FocusedAt,job.data_end_date DataEndDate,
                   job.required_daily_bars RequiredDailyBars,
                   job.adjust_mode AdjustMode,job.algorithm_version AlgorithmVersion,
                   job.attempt_count AttemptCount
            FROM wave_bottom_collection_job job
            JOIN pair_trend_live_event event ON event.id=job.event_id
            WHERE job.algorithm_version=@WaveAlgorithmVersion
              AND event.algorithm_version='pair-trend-v3'
              AND event.pivot_type='BOTTOM'
              AND event.stage IN ('FOCUS','ESTABLISHED')
              AND event.is_active=TRUE AND event.focused_at=job.focused_at
              AND (
                    (job.status IN ('PENDING','RETRY') AND
                     (job.next_attempt_at IS NULL OR job.next_attempt_at<=UTC_TIMESTAMP(6)))
                    OR
                    (job.status='LEASED' AND job.lease_expires_at<UTC_TIMESTAMP(6))
                  )
            ORDER BY job.id
            LIMIT @Maximum
            FOR UPDATE SKIP LOCKED;
            """,
            new
            {
                Maximum = maximum,
                WaveAlgorithmVersion = WaveBottomOptions.CurrentAlgorithmVersion
            }, transaction,
            cancellationToken: cancellationToken))).ToArray();

        if (rows.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new WaveBottomClaimResponse(null, [], maximum, MaximumBarsPerBatch);
        }

        var ids = rows.Select(static row => row.JobId).ToArray();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE wave_bottom_collection_job
            SET status='LEASED',lease_token=@LeaseToken,lease_owner=@CollectorId,
                lease_expires_at=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 30 MINUTE),
                attempt_count=attempt_count+1,last_error=NULL
            WHERE id IN @Ids;

            UPDATE pair_trend_live_event event
            JOIN wave_bottom_collection_job job ON job.event_id=event.id
            SET event.wave_calculation_status='COLLECTING'
            WHERE job.id IN @Ids
              AND job.algorithm_version=@WaveAlgorithmVersion
              AND event.algorithm_version='pair-trend-v3'
              AND event.pivot_type='BOTTOM'
              AND event.stage IN ('FOCUS','ESTABLISHED')
              AND event.is_active=TRUE AND event.focused_at=job.focused_at
              AND (event.wave_calculation_status<>'COMPLETED'
                   OR event.wave_algorithm_version<>@WaveAlgorithmVersion
                   OR event.wave_algorithm_version IS NULL);
            """,
            new
            {
                LeaseToken = leaseToken,
                CollectorId = collectorId,
                Ids = ids,
                WaveAlgorithmVersion = WaveBottomOptions.CurrentAlgorithmVersion
            },
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);

        var jobs = rows.Select(static row => new WaveBottomClaimJob(
            row.JobId, row.EventId, row.Symbol, row.FocusedAt,
            DateOnly.FromDateTime(row.DataEndDate), row.RequiredDailyBars,
            row.AdjustMode, row.AlgorithmVersion)).ToArray();
        _sessions[leaseToken] = new LeaseSession(
            DateTime.UtcNow.Add(LeaseDuration),
            jobs);
        return new WaveBottomClaimResponse(leaseToken, jobs, maximum, MaximumBarsPerBatch);
    }

    public async Task<int> AcceptBatchAsync(
        WaveBottomBatchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Bars is null || request.Bars.Count == 0)
            throw new ArgumentException("bars 不能为空。", nameof(request));
        if (request.Bars.Count > MaximumBarsPerBatch)
            throw new ArgumentException($"单批日K不能超过 {MaximumBarsPerBatch} 条。", nameof(request));
        var session = RequireSession(request.LeaseToken);
        lock (session.Gate)
        {
            foreach (var bar in request.Bars)
            {
                ValidateBasicBar(bar, session);
                var key = (bar.Symbol.ToUpperInvariant(), bar.TradingDate);
                if (session.Bars.TryGetValue(key, out var existing))
                {
                    if (!string.Equals(existing.SourceRowHash, bar.SourceRowHash, StringComparison.Ordinal))
                        throw new InvalidOperationException($"{bar.Symbol}/{bar.TradingDate} 日K重复且哈希冲突。");
                    continue;
                }
                session.Bars.Add(key, bar);
            }
            session.ExpiresAt = DateTime.UtcNow.Add(LeaseDuration);
        }
        await ExtendLeaseAsync(request.LeaseToken, cancellationToken);
        return request.Bars.Count;
    }

    public async Task<WaveBottomAcceptedResponse> CompleteAsync(
        WaveBottomCompleteRequest request,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(request.LeaseToken);
        var failures = (request.Failures ?? [])
            .GroupBy(static item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last().Error,
                StringComparer.OrdinalIgnoreCase);
        var unexpectedFailure = failures.Keys.FirstOrDefault(symbol =>
            !session.LatestDataEndBySymbol.ContainsKey(symbol));
        if (unexpectedFailure is not null)
            throw new ArgumentException($"{unexpectedFailure} 不属于当前波段租约。", nameof(request));
        var completed = 0;
        var retrying = 0;
        var failed = 0;
        try
        {
            foreach (var job in session.Jobs)
            {
                if (failures.TryGetValue(job.Symbol, out var error))
                {
                    var final = await MarkFailureAsync(job, error, false, cancellationToken);
                    if (final) failed++; else retrying++;
                    continue;
                }

                try
                {
                    WaveBottomDailyBar[] dailyBars;
                    lock (session.Gate)
                    {
                        dailyBars = session.Bars
                            .Where(pair => string.Equals(pair.Key.Symbol, job.Symbol,
                                StringComparison.OrdinalIgnoreCase) &&
                                pair.Value.TradingDate <= job.DataEndDate)
                            .Select(static pair => pair.Value)
                            .OrderBy(static bar => bar.TradingDate)
                            .TakeLast(job.RequiredDailyBars)
                            .ToArray();
                    }
                    ValidateCompleteBars(job, dailyBars);
                    var evaluation = _scorer.Evaluate(dailyBars.Select(ToPairTrendBar).ToArray());
                    if (await PersistEvaluationAsync(job, evaluation, cancellationToken))
                        completed++;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    var final = await MarkFailureAsync(job, exception.Message, false, cancellationToken);
                    if (final) failed++; else retrying++;
                }
            }
        }
        finally
        {
            _sessions.TryRemove(request.LeaseToken, out _);
        }
        return new WaveBottomAcceptedResponse("completed", session.Bars.Count, completed, retrying, failed);
    }

    public async Task<WaveBottomAcceptedResponse> FailLeaseAsync(
        WaveBottomLeaseFailureRequest request,
        CancellationToken cancellationToken)
    {
        // A WebAPI restart intentionally drops uploaded daily bars, but the DB
        // lease remains durable. Restore only job identities here so the local
        // collector can release the orphaned lease immediately instead of
        // waiting 30 minutes before retrying the history request.
        var session = await RequireOrRestoreLeaseSessionAsync(
            request.LeaseToken, cancellationToken);
        var retrying = 0;
        var failed = 0;
        try
        {
            foreach (var job in session.Jobs)
            {
                var final = await MarkFailureAsync(
                    job, request.Error, request.ProviderUnavailable, cancellationToken);
                if (final) failed++; else retrying++;
            }
        }
        finally
        {
            _sessions.TryRemove(request.LeaseToken, out _);
        }
        return new WaveBottomAcceptedResponse("released", 0, 0, retrying, failed);
    }

    private LeaseSession RequireSession(string leaseToken)
    {
        if (string.IsNullOrWhiteSpace(leaseToken) ||
            !_sessions.TryGetValue(leaseToken.Trim(), out var session))
            throw new KeyNotFoundException("波段采集租约不存在或API已重启，请重新领取。" );
        if (session.ExpiresAt < DateTime.UtcNow)
        {
            _sessions.TryRemove(leaseToken, out _);
            throw new InvalidOperationException("波段采集租约已过期，请重新领取。" );
        }
        return session;
    }

    private async Task<LeaseSession> RequireOrRestoreLeaseSessionAsync(
        string leaseToken,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(leaseToken) &&
            _sessions.TryGetValue(leaseToken.Trim(), out var current))
            return current;
        leaseToken = leaseToken?.Trim() ?? string.Empty;
        if (leaseToken.Length != 36)
            throw new KeyNotFoundException("波段采集租约不存在。" );
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<JobRow>(new CommandDefinition(
            """
            SELECT id JobId,event_id EventId,symbol Symbol,focused_at FocusedAt,
                   data_end_date DataEndDate,required_daily_bars RequiredDailyBars,
                   adjust_mode AdjustMode,algorithm_version AlgorithmVersion,
                   attempt_count AttemptCount
            FROM wave_bottom_collection_job
            WHERE lease_token=@LeaseToken AND status='LEASED'
              AND algorithm_version=@WaveAlgorithmVersion;
            """, new
            {
                LeaseToken = leaseToken,
                WaveAlgorithmVersion = WaveBottomOptions.CurrentAlgorithmVersion
            },
            cancellationToken: cancellationToken))).ToArray();
        if (rows.Length == 0)
            throw new KeyNotFoundException("波段采集租约不存在或已被回收。" );
        var jobs = rows.Select(static row => new WaveBottomClaimJob(
            row.JobId, row.EventId, row.Symbol, row.FocusedAt,
            DateOnly.FromDateTime(row.DataEndDate), row.RequiredDailyBars,
            row.AdjustMode, row.AlgorithmVersion)).ToArray();
        return _sessions.GetOrAdd(
            leaseToken,
            _ => new LeaseSession(DateTime.UtcNow.Add(LeaseDuration), jobs));
    }

    private async Task ExtendLeaseAsync(string leaseToken, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE wave_bottom_collection_job
            SET lease_expires_at=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 30 MINUTE)
            WHERE lease_token=@LeaseToken AND status='LEASED'
              AND algorithm_version=@WaveAlgorithmVersion;
            """, new
            {
                LeaseToken = leaseToken,
                WaveAlgorithmVersion = WaveBottomOptions.CurrentAlgorithmVersion
            }, cancellationToken: cancellationToken));
        if (affected == 0) throw new InvalidOperationException("波段采集数据库租约已失效。" );
    }

    private async Task<bool> PersistEvaluationAsync(
        WaveBottomClaimJob job,
        WaveBottomEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(job.AlgorithmVersion, WaveBottomOptions.CurrentAlgorithmVersion,
                StringComparison.Ordinal) ||
            !string.Equals(evaluation.AlgorithmVersion, WaveBottomOptions.CurrentAlgorithmVersion,
                StringComparison.Ordinal))
            throw new InvalidOperationException($"波段任务 {job.JobId} 算法版本不是当前正式版本。" );
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var eventAffected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_live_event
            SET wave_calculation_status=@CalculationStatus,wave_signal=@Signal,
                wave_score=@Score,wave_evaluated_at=UTC_TIMESTAMP(6),
                wave_data_as_of=@DataAsOf,wave_algorithm_version=@AlgorithmVersion,
                wave_input_hash=@InputHash,wave_components=@ComponentsJson,
                wave_revision=wave_revision+1
            WHERE id=@EventId AND algorithm_version='pair-trend-v3'
              AND pivot_type='BOTTOM' AND stage IN ('FOCUS','ESTABLISHED')
              AND is_active=TRUE
              AND focused_at=@FocusedAt;
            """,
            new
            {
                evaluation.CalculationStatus,
                evaluation.Signal,
                evaluation.Score,
                evaluation.DataAsOf,
                evaluation.AlgorithmVersion,
                evaluation.InputHash,
                evaluation.ComponentsJson,
                job.EventId,
                job.FocusedAt,
                job.JobId
            }, transaction, cancellationToken: cancellationToken));
        if (eventAffected != 1)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE pair_trend_live_event
                SET wave_calculation_status='NOT_ELIGIBLE',wave_signal=NULL,
                    wave_score=NULL,wave_evaluated_at=NULL,wave_data_as_of=NULL,
                    wave_algorithm_version=NULL,wave_input_hash=NULL,
                    wave_components=NULL,wave_revision=wave_revision+1
                WHERE id=@EventId AND wave_calculation_status='COLLECTING';

                UPDATE wave_bottom_collection_job
                SET status='SUPERSEDED',lease_token=NULL,lease_owner=NULL,
                    lease_expires_at=NULL,next_attempt_at=NULL,
                    last_error='事件已不再是有效重点或成立底部，跳过过期评分'
                WHERE id=@JobId AND algorithm_version=@WaveAlgorithmVersion;
                """,
                new
                {
                    job.JobId,
                    job.EventId,
                    WaveAlgorithmVersion = WaveBottomOptions.CurrentAlgorithmVersion
                }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("波段历史任务 {JobId}/{Symbol} 因事件状态变化已跳过。",
                job.JobId, job.Symbol);
            return false;
        }

        var jobAffected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE wave_bottom_collection_job
            SET status='COMPLETED',lease_token=NULL,lease_owner=NULL,
                lease_expires_at=NULL,next_attempt_at=NULL,last_error=NULL,
                completed_at=UTC_TIMESTAMP(6)
            WHERE id=@JobId AND algorithm_version=@WaveAlgorithmVersion
              AND status='LEASED';
            """,
            new
            {
                job.JobId,
                WaveAlgorithmVersion = WaveBottomOptions.CurrentAlgorithmVersion
            }, transaction, cancellationToken: cancellationToken));
        if (jobAffected != 1)
            throw new InvalidOperationException($"波段任务 {job.JobId} 租约已失效，拒绝提交评分。" );
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> MarkFailureAsync(
        WaveBottomClaimJob job,
        string error,
        bool providerUnavailable,
        CancellationToken cancellationToken)
    {
        error = error.Trim();
        if (error.Length > 2_000) error = error[..2_000];
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var attempt = await connection.QuerySingleAsync<int>(new CommandDefinition(
            """
            SELECT attempt_count FROM wave_bottom_collection_job
            WHERE id=@JobId AND algorithm_version=@WaveAlgorithmVersion FOR UPDATE;
            """,
            new
            {
                job.JobId,
                WaveAlgorithmVersion = WaveBottomOptions.CurrentAlgorithmVersion
            }, transaction, cancellationToken: cancellationToken));
        if (providerUnavailable)
        {
            attempt = Math.Max(0, attempt - 1);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE wave_bottom_collection_job SET attempt_count=@Attempt WHERE id=@JobId;",
                new { Attempt = attempt, job.JobId }, transaction,
                cancellationToken: cancellationToken));
        }
        var final = !providerUnavailable && attempt >= MaximumAttempts;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE wave_bottom_collection_job
            SET status=@Status,lease_token=NULL,lease_owner=NULL,lease_expires_at=NULL,
                next_attempt_at=CASE WHEN @Final THEN NULL
                    ELSE DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 5 MINUTE) END,
                last_error=@Error
            WHERE id=@JobId;

            UPDATE pair_trend_live_event
            SET wave_calculation_status=CASE WHEN @Final THEN 'FAILED' ELSE 'PENDING' END
            WHERE id=@EventId AND algorithm_version='pair-trend-v3'
              AND pivot_type='BOTTOM' AND stage IN ('FOCUS','ESTABLISHED')
              AND is_active=TRUE
              AND wave_calculation_status<>'COMPLETED';

            UPDATE wave_bottom_collection_job job
            JOIN pair_trend_live_event event ON event.id=job.event_id
            SET job.status='SUPERSEDED',job.lease_token=NULL,job.lease_owner=NULL,
                job.lease_expires_at=NULL,job.next_attempt_at=NULL,
                job.last_error='事件已不再是有效重点或成立底部，停止重试',
                event.wave_calculation_status=CASE
                    WHEN event.wave_calculation_status='COLLECTING' THEN 'NOT_ELIGIBLE'
                    ELSE event.wave_calculation_status END
            WHERE job.id=@JobId AND job.algorithm_version=@WaveAlgorithmVersion
              AND NOT(event.algorithm_version='pair-trend-v3'
                      AND event.pivot_type='BOTTOM'
                      AND event.stage IN ('FOCUS','ESTABLISHED')
                      AND event.is_active=TRUE AND event.focused_at=job.focused_at);
            """,
            new
            {
                Status = final ? "FAILED" : "RETRY",
                Final = final,
                Error = error,
                job.JobId,
                job.EventId,
                WaveAlgorithmVersion = WaveBottomOptions.CurrentAlgorithmVersion
            }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        logger.LogWarning(
            "波段历史任务 {JobId}/{Symbol} {Status}: {Error}",
            job.JobId, job.Symbol, final ? "FAILED" : "RETRY", error);
        return final;
    }

    private static void ValidateBasicBar(WaveBottomDailyBar bar, LeaseSession session)
    {
        if (!session.LatestDataEndBySymbol.TryGetValue(bar.Symbol, out var latestDataEnd))
            throw new ArgumentException($"{bar.Symbol} 不属于当前波段租约。" );
        if (bar.TradingDate > latestDataEnd)
            throw new ArgumentException($"{bar.Symbol}/{bar.TradingDate} 晚于任务截止日。" );
        if (bar.OpenPrice <= 0 || bar.HighPrice <= 0 || bar.LowPrice <= 0 || bar.ClosePrice <= 0 ||
            bar.HighPrice < Math.Max(bar.OpenPrice, bar.ClosePrice) ||
            bar.LowPrice > Math.Min(bar.OpenPrice, bar.ClosePrice) ||
            bar.Volume < 0 || bar.Amount < 0)
            throw new ArgumentException($"{bar.Symbol}/{bar.TradingDate} OHLCV不合法。" );
        if (bar.SourceRowHash.Length != 64)
            throw new ArgumentException($"{bar.Symbol}/{bar.TradingDate} 来源哈希无效。" );
    }

    private static void ValidateCompleteBars(
        WaveBottomClaimJob job,
        IReadOnlyCollection<WaveBottomDailyBar> bars)
    {
        if (bars.Count > job.RequiredDailyBars)
            throw new InvalidOperationException(
                $"{job.Symbol} 返回 {bars.Count} 根日K，超过任务要求 {job.RequiredDailyBars}。" );
        if (bars.Select(static bar => bar.TradingDate).Distinct().Count() != bars.Count)
            throw new InvalidOperationException($"{job.Symbol} 存在重复交易日日K。" );
        if (bars.Any(bar => !string.Equals(bar.Symbol, job.Symbol, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{job.Symbol} 混入其他证券日K。" );
    }

    private static PairTrendBar ToPairTrendBar(WaveBottomDailyBar bar)
    {
        var tradingDate = bar.TradingDate.ToDateTime(TimeOnly.MinValue);
        return new PairTrendBar(
            bar.Symbol, "1d", tradingDate,
            tradingDate.AddHours(9).AddMinutes(30), tradingDate.AddHours(15),
            bar.OpenPrice, bar.HighPrice, bar.LowPrice, bar.ClosePrice,
            bar.PreClose, bar.Volume, bar.Amount, bar.SourceRowHash);
    }

    private sealed class LeaseSession(
        DateTime expiresAt,
        IReadOnlyCollection<WaveBottomClaimJob> jobs)
    {
        public object Gate { get; } = new();
        public DateTime ExpiresAt { get; set; } = expiresAt;
        public IReadOnlyCollection<WaveBottomClaimJob> Jobs { get; } = jobs;
        public IReadOnlyDictionary<string, DateOnly> LatestDataEndBySymbol { get; } = jobs
            .GroupBy(static job => job.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Max(static job => job.DataEndDate),
                StringComparer.OrdinalIgnoreCase);
        public Dictionary<(string Symbol, DateOnly TradingDate), WaveBottomDailyBar> Bars { get; } = [];
    }

    private sealed class JobRow
    {
        public long JobId { get; init; }
        public long EventId { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public DateTime FocusedAt { get; init; }
        public DateTime DataEndDate { get; init; }
        public int RequiredDailyBars { get; init; }
        public string AdjustMode { get; init; } = string.Empty;
        public string AlgorithmVersion { get; init; } = string.Empty;
        public int AttemptCount { get; init; }
    }
}
