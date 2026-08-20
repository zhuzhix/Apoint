using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using AStockMonitor.Api.Models;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;

namespace AStockMonitor.Api.Services;

/// <summary>
/// 成立事件次一交易日验证的唯一业务实现。采集端只上传指定日期的官方5分钟K线；
/// 验证、历史修订、幂等和审计全部由 WebAPI 完成。
/// </summary>
public sealed class PairTrendNextDayValidationService(
    IMySqlConnectionFactory connectionFactory,
    PairTrendQueryCache queryCache,
    ILogger<PairTrendNextDayValidationService> logger)
{
    public const int MaximumSymbolsPerClaim = 200;
    public const int MaximumBarsPerBatch = 2_000;
    public const int SparseConfirmations = 3;
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, LeaseSession> _sessions = new(StringComparer.Ordinal);

    public async Task<NextDayValidationRunResponse> CreateHistoricalRunAsync(
        NextDayValidationCreateRunRequest request,
        CancellationToken cancellationToken)
    {
        var today = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
            DateTimeOffset.UtcNow,
            OperatingSystem.IsWindows() ? "China Standard Time" : "Asia/Shanghai").Date;
        if (request.DateFrom > request.DateTo)
            throw new ArgumentException("dateFrom 不能晚于 dateTo。", nameof(request));
        if (request.DateTo >= DateOnly.FromDateTime(today))
            throw new ArgumentException("历史验证只接受已经完整收盘的交易日。", nameof(request));
        if (request.DateTo.DayNumber - request.DateFrom.DayNumber > 366)
            throw new ArgumentException("单次历史验证范围不能超过367个自然日。", nameof(request));
        if (request.TradingDates is null)
            throw new ArgumentException("交易日历不能为空。", nameof(request));
        var suppliedTradingDates = request.TradingDates.ToArray();
        var tradingDates = suppliedTradingDates.Distinct().Order().ToArray();
        if (tradingDates.Length == 0 ||
            tradingDates.Length != suppliedTradingDates.Length ||
            !tradingDates.SequenceEqual(suppliedTradingDates))
            throw new ArgumentException("交易日历不能为空、重复或无序。", nameof(request));
        if (tradingDates[^1] > request.DateTo)
            throw new ArgumentException("交易日历不能包含验证范围结束日之后的日期。", nameof(request));
        if (!tradingDates.Any(date => date < request.DateFrom))
            throw new ArgumentException("交易日历必须至少包含一个早于dateFrom的交易日。", nameof(request));

        var runKeyMaterial = $"HISTORICAL|{request.DateFrom:yyyy-MM-dd}|{request.DateTo:yyyy-MM-dd}|{request.ApplyChanges}|{Guid.NewGuid():N}";
        var runKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runKeyMaterial))).ToLowerInvariant();
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        var runId = await connection.QuerySingleAsync<long>(new CommandDefinition(
            """
            INSERT INTO pair_trend_next_day_validation_run(
                run_key,run_mode,date_from,date_to,apply_changes,status)
            VALUES(@RunKey,'HISTORICAL',@DateFrom,@DateTo,@ApplyChanges,'PREPARED');
            SELECT LAST_INSERT_ID();
            """,
            new
            {
                RunKey = runKey,
                DateFrom = request.DateFrom.ToDateTime(TimeOnly.MinValue),
                DateTo = request.DateTo.ToDateTime(TimeOnly.MinValue),
                request.ApplyChanges
            }, transaction, cancellationToken: cancellationToken));

        var sourceEvents = (await connection.QueryAsync<SeedEventRow>(new CommandDefinition(
            """
            SELECT e.id EventId,e.symbol Symbol,e.pivot_type PivotType,
                   e.latest_pair_price PairPrice,DATE(e.established_at) EstablishedTradingDate,
                   e.invalidated_at InvalidatedAt
            FROM pair_trend_live_event e
            WHERE e.algorithm_version='pair-trend-v3'
              AND e.established_at IS NOT NULL
              AND DATE(e.established_at)>=@CalendarStart
              AND DATE(e.established_at)<@DateToExclusive
            ORDER BY e.established_at,e.symbol,e.id;
            """,
            new
            {
                CalendarStart = tradingDates[0].ToDateTime(TimeOnly.MinValue),
                DateToExclusive = request.DateTo.AddDays(1).ToDateTime(TimeOnly.MinValue)
            }, transaction, cancellationToken: cancellationToken))).ToArray();
        var events = sourceEvents.Select(row =>
            {
                var establishedDate = DateOnly.FromDateTime(row.EstablishedTradingDate);
                var validationDate = tradingDates.FirstOrDefault(date => date > establishedDate);
                return validationDate == default || validationDate < request.DateFrom || validationDate > request.DateTo
                    ? null
                    : new SeedEventRow
                    {
                        EventId = row.EventId,
                        Symbol = row.Symbol,
                        PivotType = row.PivotType,
                        PairPrice = row.PairPrice,
                        EstablishedTradingDate = row.EstablishedTradingDate,
                        ValidationTradingDate = validationDate.ToDateTime(TimeOnly.MinValue),
                        InvalidatedAt = row.InvalidatedAt
                    };
            })
            .Where(static row => row is not null)
            .Select(static row => row!)
            .OrderBy(static row => row.ValidationTradingDate)
            .ThenBy(static row => row.Symbol, StringComparer.Ordinal)
            .ThenBy(static row => row.EventId)
            .ToArray();

        if (events.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO pair_trend_next_day_validation(
                    run_id,event_id,symbol,pivot_type,pair_price,
                    established_trading_date,validation_trading_date,status,completed_at)
                VALUES(
                    @RunId,@EventId,@Symbol,@PivotType,@PairPrice,
                    @EstablishedTradingDate,@ValidationTradingDate,@Status,@CompletedAt);
                """,
                events.Select(row => new
                {
                    RunId = runId,
                    row.EventId,
                    row.Symbol,
                    row.PivotType,
                    row.PairPrice,
                    row.EstablishedTradingDate,
                    row.ValidationTradingDate,
                    Status = row.InvalidatedAt is not null &&
                             row.InvalidatedAt < row.ValidationTradingDate.Date.AddHours(9).AddMinutes(30)
                        ? "NOT_APPLICABLE"
                        : "PENDING",
                    CompletedAt = row.InvalidatedAt is not null &&
                                  row.InvalidatedAt < row.ValidationTradingDate.Date.AddHours(9).AddMinutes(30)
                        ? DateTime.UtcNow
                        : (DateTime?)null
                }), transaction, cancellationToken: cancellationToken));
        }

        await RefreshRunAsync(connection, transaction, runId, cancellationToken);
        if (request.ApplyChanges)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE pair_trend_live_event event
                JOIN pair_trend_next_day_validation validation ON validation.event_id=event.id
                SET event.next_day_validation_date=validation.validation_trading_date,
                    event.next_day_validation_status='NOT_APPLICABLE',
                    event.next_day_validation_checked_at=UTC_TIMESTAMP(6)
                WHERE validation.run_id=@RunId AND validation.status='NOT_APPLICABLE';
                """, new { RunId = runId }, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "创建历史次日验证运行 {RunId}：{DateFrom} 至 {DateTo}，apply={ApplyChanges}，事件={Count}。",
            runId, request.DateFrom, request.DateTo, request.ApplyChanges, events.Length);
        return await GetRunAsync(runId, cancellationToken);
    }

    public async Task<NextDayValidationRunResponse> GetRunAsync(
        long runId,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            """
            SELECT id RunId,status Status,date_from DateFrom,date_to DateTo,
                   apply_changes ApplyChanges,total_count Total,completed_count Completed,
                   invalidated_count Invalidated,passed_count Passed,no_trade_count NoTrade,
                   not_applicable_count NotApplicable,failed_count Failed,last_error LastError
            FROM pair_trend_next_day_validation_run WHERE id=@RunId;
            """, new { RunId = runId }, cancellationToken: cancellationToken));
        if (row is null) throw new KeyNotFoundException("次日验证运行不存在。" );
        return row.ToResponse();
    }

    public async Task<NextDayValidationRunResponse> PrepareRealtimeAsync(
        DateOnly tradingDate,
        DateOnly previousTradingDate,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
            DateTimeOffset.UtcNow,
            OperatingSystem.IsWindows() ? "China Standard Time" : "Asia/Shanghai").Date);
        if (tradingDate != today || previousTradingDate >= tradingDate)
            throw new ArgumentException("实时次日验证必须使用今天和官方紧邻的上一交易日。");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"REALTIME|{previousTradingDate:yyyy-MM-dd}|{tradingDate:yyyy-MM-dd}"))).ToLowerInvariant();
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var existing = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT id FROM pair_trend_next_day_validation_run WHERE run_key=@Key FOR UPDATE;",
            new { Key = key }, transaction, cancellationToken: cancellationToken));
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return await GetRunAsync(existing.Value, cancellationToken);
        }

        var runId = await connection.QuerySingleAsync<long>(new CommandDefinition(
            """
            INSERT INTO pair_trend_next_day_validation_run(
                run_key,run_mode,date_from,date_to,apply_changes,status)
            VALUES(@Key,'REALTIME',@PreviousTradingDate,@TradingDate,TRUE,'PREPARED');
            SELECT LAST_INSERT_ID();
            """, new
            {
                Key = key,
                PreviousTradingDate = previousTradingDate.ToDateTime(TimeOnly.MinValue),
                TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue)
            }, transaction, cancellationToken: cancellationToken));
        var events = (await connection.QueryAsync<SeedEventRow>(new CommandDefinition(
            """
            SELECT id EventId,symbol Symbol,pivot_type PivotType,
                   latest_pair_price PairPrice,DATE(established_at) EstablishedTradingDate,
                   @TradingDate ValidationTradingDate,invalidated_at InvalidatedAt
            FROM pair_trend_live_event
            WHERE algorithm_version='pair-trend-v3'
              AND established_at IS NOT NULL
              AND DATE(established_at)=@PreviousTradingDate
            ORDER BY symbol,id;
            """, new
            {
                PreviousTradingDate = previousTradingDate.ToDateTime(TimeOnly.MinValue),
                TradingDate = tradingDate.ToDateTime(TimeOnly.MinValue)
            }, transaction, cancellationToken: cancellationToken))).ToArray();
        if (events.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO pair_trend_next_day_validation(
                    run_id,event_id,symbol,pivot_type,pair_price,
                    established_trading_date,validation_trading_date,status,completed_at)
                VALUES(
                    @RunId,@EventId,@Symbol,@PivotType,@PairPrice,
                    @EstablishedTradingDate,@ValidationTradingDate,@Status,@CompletedAt);
                """, events.Select(row => new
                {
                    RunId = runId,
                    row.EventId,
                    row.Symbol,
                    row.PivotType,
                    row.PairPrice,
                    row.EstablishedTradingDate,
                    row.ValidationTradingDate,
                    Status = row.InvalidatedAt is not null &&
                             row.InvalidatedAt < row.ValidationTradingDate.Date.AddHours(9).AddMinutes(30)
                        ? "NOT_APPLICABLE" : "PENDING",
                    CompletedAt = row.InvalidatedAt is not null &&
                                  row.InvalidatedAt < row.ValidationTradingDate.Date.AddHours(9).AddMinutes(30)
                        ? DateTime.UtcNow : (DateTime?)null
                }), transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE pair_trend_live_event event
                JOIN pair_trend_next_day_validation validation ON validation.event_id=event.id
                SET event.next_day_validation_date=validation.validation_trading_date,
                    event.next_day_validation_status='NOT_APPLICABLE',
                    event.next_day_validation_checked_at=UTC_TIMESTAMP(6)
                WHERE validation.run_id=@RunId AND validation.status='NOT_APPLICABLE';
                """, new { RunId = runId }, transaction, cancellationToken: cancellationToken));
        }
        await RefreshRunAsync(connection, transaction, runId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "准备实时次日验证 {RunId}：上一交易日 {PreviousTradingDate}，验证日 {TradingDate}，事件 {Count}。",
            runId, previousTradingDate, tradingDate, events.Length);
        return await GetRunAsync(runId, cancellationToken);
    }

    public async Task ProcessRealtimeSnapshotAsync(
        PairTrendCollectionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var window = snapshot.Windows.FirstOrDefault(static item =>
            item.Frequency.Equals("5m", StringComparison.OrdinalIgnoreCase));
        if (window is null) return;
        var expected = ExpectedEobs(snapshot.TradingDate)
            .Where(eob => eob <= window.To).Order().ToArray();
        if (expected.Length == 0) return;
        var final = window.To >= snapshot.TradingDate.ToDateTime(new TimeOnly(15, 0));

        await using var connection = connectionFactory.Create();
        var runId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            """
            SELECT id FROM pair_trend_next_day_validation_run
            WHERE run_mode='REALTIME' AND date_to=@TradingDate
            ORDER BY id DESC LIMIT 1;
            """, new { TradingDate = snapshot.TradingDate.ToDateTime(TimeOnly.MinValue) },
            cancellationToken: cancellationToken));
        if (runId is null)
            throw new InvalidOperationException(
                $"{snapshot.TradingDate:yyyy-MM-dd} 尚未用官方上一交易日准备实时验证运行。");
        var jobs = (await connection.QueryAsync<JobRow>(new CommandDefinition(
            """
            SELECT id ValidationId,event_id EventId,symbol Symbol,pivot_type PivotType,
                   pair_price PairPrice,established_trading_date EstablishedTradingDate,
                   validation_trading_date ValidationTradingDate,attempt_count AttemptCount
            FROM pair_trend_next_day_validation
            WHERE run_id=@RunId AND status IN ('PENDING','MONITORING')
            ORDER BY symbol,id;
            """, new { RunId = runId.Value }, cancellationToken: cancellationToken))).ToArray();
        if (jobs.Length == 0)
        {
            await RefreshRunAsync(runId.Value, cancellationToken);
            return;
        }
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_next_day_validation_run
            SET status='RUNNING',started_at=COALESCE(started_at,UTC_TIMESTAMP(6))
            WHERE id=@RunId AND status='PREPARED';
            """, new { RunId = runId.Value }, cancellationToken: cancellationToken));

        var bySymbol = snapshot.Symbols.ToDictionary(static item => item.Symbol,
            StringComparer.OrdinalIgnoreCase);
        var invalidated = 0;
        foreach (var job in jobs)
        {
            if (!bySymbol.TryGetValue(job.Symbol, out var symbol))
                throw new InvalidOperationException($"实时次日验证证券 {job.Symbol} 不在本轮内存快照。" );
            var bars = symbol.BarsByFrequency.TryGetValue("5m", out var fiveMinute)
                ? fiveMinute.Where(item => item.Eob <= window.To)
                    .Select(static item => new PairTrendNextDayBar(
                        item.Eob, item.HighPrice, item.LowPrice, item.SourceRowHash)).ToArray()
                : [];
            var missing = symbol.VerifiedMissingEobsByFrequency.TryGetValue("5m", out var verified)
                ? verified.Where(item => item <= window.To).ToArray()
                : [];
            var evaluation = PairTrendNextDayValidationEvaluator.EvaluateRealtime(
                job.PivotType, job.PairPrice, bars, missing, expected, final);
            await PersistEvaluationAsync(
                true, snapshot.TradingDate, job, evaluation, true, cancellationToken);
            if (evaluation.Status == "INVALIDATED") invalidated++;
        }
        await RefreshRunAsync(runId.Value, cancellationToken);
        queryCache.Invalidate();
        logger.LogInformation(
            "实时次日验证 {RunId}/{TradingDate} 已处理至 {Watermark}：事件 {Count}，本轮失效 {Invalidated}，final={Final}。",
            runId, snapshot.TradingDate, window.To, jobs.Length, invalidated, final);
    }

    public async Task<NextDayValidationClaimResponse> ClaimAsync(
        long runId,
        string collectorId,
        int requestedMaximum,
        CancellationToken cancellationToken)
    {
        collectorId = collectorId.Trim();
        if (collectorId.Length is < 3 or > 128)
            throw new ArgumentException("collectorId 长度无效。", nameof(collectorId));
        var maximum = Math.Clamp(requestedMaximum, 1, MaximumSymbolsPerClaim);
        var leaseToken = Guid.NewGuid().ToString();
        foreach (var expired in _sessions.Where(static item => item.Value.ExpiresAt < DateTime.UtcNow)
                     .Select(static item => item.Key).ToArray())
            _sessions.TryRemove(expired, out _);

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var run = await connection.QuerySingleOrDefaultAsync<RunLeaseRow>(new CommandDefinition(
            """
            SELECT id RunId,run_mode RunMode,status Status,apply_changes ApplyChanges
            FROM pair_trend_next_day_validation_run WHERE id=@RunId FOR UPDATE;
            """, new { RunId = runId }, transaction, cancellationToken: cancellationToken));
        if (run is null) throw new KeyNotFoundException("次日验证运行不存在。" );
        if (run.RunMode != "HISTORICAL")
            throw new InvalidOperationException("实时验证运行不能由历史采集租约领取。" );
        if (run.Status is "COMPLETED" or "COMPLETED_WITH_ERRORS" or "FAILED")
        {
            await transaction.CommitAsync(cancellationToken);
            return new NextDayValidationClaimResponse(runId, null, null, run.ApplyChanges, [], maximum, MaximumBarsPerBatch);
        }

        var validationDate = await connection.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            """
            SELECT MIN(validation_trading_date)
            FROM pair_trend_next_day_validation
            WHERE run_id=@RunId AND (
                status IN ('PENDING','RETRY') OR
                (status='LEASED' AND lease_expires_at<UTC_TIMESTAMP(6)));
            """, new { RunId = runId }, transaction, cancellationToken: cancellationToken));
        if (validationDate is null)
        {
            await RefreshRunAsync(connection, transaction, runId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new NextDayValidationClaimResponse(runId, null, null, run.ApplyChanges, [], maximum, MaximumBarsPerBatch);
        }

        var symbols = (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT symbol
            FROM pair_trend_next_day_validation
            WHERE run_id=@RunId AND validation_trading_date=@ValidationDate AND (
                status IN ('PENDING','RETRY') OR
                (status='LEASED' AND lease_expires_at<UTC_TIMESTAMP(6)))
            GROUP BY symbol ORDER BY symbol LIMIT @Maximum;
            """, new { RunId = runId, ValidationDate = validationDate, Maximum = maximum },
            transaction, cancellationToken: cancellationToken))).ToArray();
        var jobs = (await connection.QueryAsync<JobRow>(new CommandDefinition(
            """
            SELECT id ValidationId,event_id EventId,symbol Symbol,pivot_type PivotType,
                   pair_price PairPrice,established_trading_date EstablishedTradingDate,
                   validation_trading_date ValidationTradingDate,attempt_count AttemptCount
            FROM pair_trend_next_day_validation
            WHERE run_id=@RunId AND validation_trading_date=@ValidationDate
              AND symbol IN @Symbols AND (
                status IN ('PENDING','RETRY') OR
                (status='LEASED' AND lease_expires_at<UTC_TIMESTAMP(6)))
            ORDER BY symbol,id FOR UPDATE;
            """, new { RunId = runId, ValidationDate = validationDate, Symbols = symbols },
            transaction, cancellationToken: cancellationToken))).ToArray();
        if (jobs.Length == 0) throw new InvalidOperationException("次日验证领取结果为空。" );

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_next_day_validation
            SET status='LEASED',lease_token=@LeaseToken,lease_owner=@CollectorId,
                lease_expires_at=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 30 MINUTE),
                attempt_count=attempt_count+1,last_error=NULL
            WHERE id IN @Ids;
            UPDATE pair_trend_next_day_validation_run
            SET status='RUNNING',started_at=COALESCE(started_at,UTC_TIMESTAMP(6))
            WHERE id=@RunId;
            """, new
            {
                LeaseToken = leaseToken,
                CollectorId = collectorId,
                Ids = jobs.Select(static item => item.ValidationId).ToArray(),
                RunId = runId
            }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        _sessions[leaseToken] = new LeaseSession(runId, run.ApplyChanges,
            DateOnly.FromDateTime(validationDate.Value), DateTime.UtcNow.Add(LeaseDuration), jobs);
        return new NextDayValidationClaimResponse(runId, leaseToken,
            DateOnly.FromDateTime(validationDate.Value), run.ApplyChanges, symbols, maximum, MaximumBarsPerBatch);
    }

    public async Task<int> AcceptBatchAsync(
        NextDayValidationBatchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Bars.Count is < 1 or > MaximumBarsPerBatch)
            throw new ArgumentException($"单批5分钟K线必须为1-{MaximumBarsPerBatch}条。", nameof(request));
        var session = RequireSession(request.LeaseToken);
        lock (session.Gate)
        {
            foreach (var bar in request.Bars)
            {
                var symbol = bar.Symbol.Trim().ToUpperInvariant();
                if (!session.Symbols.Contains(symbol))
                    throw new ArgumentException($"{symbol} 不属于当前次日验证租约。", nameof(request));
                ValidateBar(bar, session.ValidationTradingDate);
                var key = (symbol, bar.Eob);
                if (session.Bars.TryGetValue(key, out var existing))
                {
                    if (!string.Equals(existing.SourceRowHash, bar.SourceRowHash, StringComparison.Ordinal))
                        throw new InvalidOperationException($"{symbol}/{bar.Eob:O} 重复K线哈希冲突。" );
                    continue;
                }
                session.Bars.Add(key, bar with { Symbol = symbol });
            }
            session.ExpiresAt = DateTime.UtcNow.Add(LeaseDuration);
        }
        await ExtendLeaseAsync(request.LeaseToken, cancellationToken);
        return request.Bars.Count;
    }

    public async Task<NextDayValidationAcceptedResponse> CompleteAsync(
        NextDayValidationCompleteRequest request,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(request.LeaseToken);
        var proofs = request.SparseProofs.ToDictionary(static item => item.Symbol.Trim().ToUpperInvariant(), StringComparer.Ordinal);
        var failures = (request.Failures ?? []).ToDictionary(
            static item => item.Symbol.Trim().ToUpperInvariant(), static item => item.Error, StringComparer.Ordinal);
        if (proofs.Keys.Concat(failures.Keys).Any(symbol => !session.Symbols.Contains(symbol)))
            throw new ArgumentException("完成请求包含租约外证券。", nameof(request));
        var completed = 0;
        var retrying = 0;
        var failed = 0;
        try
        {
            foreach (var symbolGroup in session.Jobs.GroupBy(static item => item.Symbol, StringComparer.Ordinal))
            {
                var symbol = symbolGroup.Key;
                if (failures.TryGetValue(symbol, out var error))
                {
                    foreach (var job in symbolGroup)
                    {
                        if (await MarkFailureAsync(job, error, cancellationToken)) failed++; else retrying++;
                    }
                    continue;
                }
                var bars = session.Bars.Where(pair => pair.Key.Symbol == symbol)
                    .Select(static pair => pair.Value).OrderBy(static item => item.Eob).ToArray();
                if (!proofs.TryGetValue(symbol, out var proof))
                    throw new InvalidOperationException($"{symbol} 缺少完整的无成交窗口证明。" );
                var missing = ValidateSparseProof(proof, bars, session.ValidationTradingDate);
                var evaluationBars = bars.Select(static item => new PairTrendNextDayBar(
                    item.Eob, item.HighPrice, item.LowPrice, item.SourceRowHash)).ToArray();
                foreach (var job in symbolGroup)
                {
                    // 同一股票同一天可能有多个代次、方向或对子价；K线共享，
                    // 但必须逐事件独立判定，绝不能用第一条事件的阈值覆盖整只股票。
                    var evaluation = PairTrendNextDayValidationEvaluator.Evaluate(
                        job.PivotType, job.PairPrice, evaluationBars, missing);
                    await PersistEvaluationAsync(
                        session.ApplyChanges, session.ValidationTradingDate,
                        job, evaluation, false, cancellationToken);
                    completed++;
                }
            }
        }
        finally
        {
            _sessions.TryRemove(request.LeaseToken, out _);
        }
        await RefreshRunAsync(session.RunId, cancellationToken);
        queryCache.Invalidate();
        logger.LogInformation(
            "次日验证租约 {LeaseToken} 完成：run={RunId} date={Date} events={Completed} retry={Retrying} failed={Failed}。",
            request.LeaseToken, session.RunId, session.ValidationTradingDate, completed, retrying, failed);
        return new NextDayValidationAcceptedResponse("completed", session.Bars.Count, completed, retrying, failed);
    }

    public async Task<NextDayValidationAcceptedResponse> FailLeaseAsync(
        NextDayValidationFailLeaseRequest request,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(request.LeaseToken);
        var retrying = 0;
        var failed = 0;
        try
        {
            foreach (var job in session.Jobs)
            {
                if (await MarkFailureAsync(job, request.Error, cancellationToken)) failed++; else retrying++;
            }
        }
        finally
        {
            _sessions.TryRemove(request.LeaseToken, out _);
        }
        await RefreshRunAsync(session.RunId, cancellationToken);
        return new NextDayValidationAcceptedResponse("released", 0, 0, retrying, failed);
    }

    private LeaseSession RequireSession(string leaseToken)
    {
        leaseToken = leaseToken?.Trim() ?? string.Empty;
        if (!_sessions.TryGetValue(leaseToken, out var session))
            throw new KeyNotFoundException("次日验证租约不存在或API已经重启。" );
        if (session.ExpiresAt < DateTime.UtcNow)
        {
            _sessions.TryRemove(leaseToken, out _);
            throw new InvalidOperationException("次日验证租约已过期。" );
        }
        return session;
    }

    private async Task ExtendLeaseAsync(string leaseToken, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_next_day_validation
            SET lease_expires_at=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 30 MINUTE)
            WHERE lease_token=@LeaseToken AND status='LEASED';
            """, new { LeaseToken = leaseToken }, cancellationToken: cancellationToken));
        if (affected == 0) throw new InvalidOperationException("次日验证数据库租约已失效。" );
    }

    private async Task<bool> MarkFailureAsync(JobRow job, string error, CancellationToken cancellationToken)
    {
        var final = job.AttemptCount + 1 >= MaximumAttempts;
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_next_day_validation
            SET status=@Status,lease_token=NULL,lease_owner=NULL,lease_expires_at=NULL,
                last_error=@Error,completed_at=IF(@Final,UTC_TIMESTAMP(6),NULL)
            WHERE id=@ValidationId AND status='LEASED';
            """, new
            {
                Status = final ? "FAILED" : "RETRY",
                Error = error.Length > 2000 ? error[..2000] : error,
                Final = final,
                job.ValidationId
            }, cancellationToken: cancellationToken));
        return final;
    }

    private async Task PersistEvaluationAsync(
        bool applyChanges,
        DateOnly validationTradingDate,
        JobRow job,
        PairTrendNextDayEvaluation evaluation,
        bool realtime,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<EventStateRow>(new CommandDefinition(
            """
            SELECT id EventId,event_key EventKey,stage Stage,is_active IsActive,
                   invalidated_at InvalidatedAt,invalidated_price InvalidatedPrice,
                   invalidation_reason InvalidationReason,last_transition_at LastTransitionAt,
                   event_revision EventRevision,content_hash ContentHash,
                   CAST(summary_json AS CHAR CHARACTER SET utf8mb4) SummaryJson,
                   pivot_type PivotType,latest_pair_price PairPrice,established_at EstablishedAt
            FROM pair_trend_live_event WHERE id=@EventId AND algorithm_version='pair-trend-v3'
            FOR UPDATE;
            """, new { job.EventId }, transaction, cancellationToken: cancellationToken));
        if (current is null || current.EstablishedAt is null ||
            current.PivotType != job.PivotType || current.PairPrice != job.PairPrice)
            throw new InvalidOperationException($"事件 {job.EventId} 在验证期间发生关键字段修订。" );

        var shouldCorrect = evaluation.Status == "INVALIDATED" &&
                            evaluation.BreachedAt is not null &&
                            (current.InvalidatedAt is null || evaluation.BreachedAt < current.InvalidatedAt);
        if (applyChanges && shouldCorrect)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO pair_trend_next_day_validation_change(
                    validation_id,event_id,previous_stage,previous_is_active,
                    previous_invalidated_at,previous_invalidated_price,
                    previous_invalidation_reason,previous_last_transition_at,
                    previous_event_revision,previous_content_hash,previous_summary_json,
                    applied_breached_at,applied_breach_price)
                VALUES(
                    @ValidationId,@EventId,@Stage,@IsActive,@InvalidatedAt,@InvalidatedPrice,
                    @InvalidationReason,@LastTransitionAt,@EventRevision,@ContentHash,
                    CAST(@SummaryJson AS JSON),@BreachedAt,@BreachPrice);
                """, new
                {
                    job.ValidationId,
                    current.EventId,
                    current.Stage,
                    current.IsActive,
                    current.InvalidatedAt,
                    current.InvalidatedPrice,
                    current.InvalidationReason,
                    current.LastTransitionAt,
                    current.EventRevision,
                    current.ContentHash,
                    current.SummaryJson,
                    evaluation.BreachedAt,
                    evaluation.BreachPrice
                }, transaction, cancellationToken: cancellationToken));
        }

        if (applyChanges)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE pair_trend_live_event
                SET next_day_validation_date=@ValidationDate,
                    next_day_validation_status=@ValidationStatus,
                    next_day_observed_extreme_price=@ObservedExtremePrice,
                    next_day_breached_at=@BreachedAt,
                    next_day_breach_price=@BreachPrice,
                    next_day_validation_checked_at=UTC_TIMESTAMP(6),
                    next_day_validation_source_hash=@SourceInputHash,
                    stage=IF(@ShouldCorrect,'INVALIDATED',stage),
                    is_active=IF(@ShouldCorrect,FALSE,is_active),
                    invalidated_at=IF(@ShouldCorrect,@BreachedAt,invalidated_at),
                    invalidated_price=IF(@ShouldCorrect,@BreachPrice,invalidated_price),
                    invalidation_reason=IF(@ShouldCorrect,@Reason,invalidation_reason),
                    last_transition_at=IF(@ShouldCorrect AND @WasActive,@BreachedAt,last_transition_at),
                    event_revision=event_revision+IF(@ShouldCorrect,1,0),
                    content_hash=IF(@ShouldCorrect,
                        SHA2(CONCAT(content_hash,':next-day:',@SourceInputHash),256),content_hash),
                    summary_json=IF(@ShouldCorrect,
                        JSON_SET(summary_json,'$.stage','INVALIDATED','$.isActive',FALSE,
                            '$.invalidatedAt',DATE_FORMAT(@BreachedAt,'%Y-%m-%dT%H:%i:%s.%f'),
                            '$.invalidatedPrice',@BreachPrice,'$.invalidationReason',@Reason),
                        summary_json)
                WHERE id=@EventId;
                """, new
                {
                    ValidationDate = validationTradingDate.ToDateTime(TimeOnly.MinValue),
                    ValidationStatus = evaluation.Status,
                    evaluation.ObservedExtremePrice,
                    evaluation.BreachedAt,
                    evaluation.BreachPrice,
                    evaluation.SourceInputHash,
                    ShouldCorrect = shouldCorrect,
                    WasActive = current.IsActive && current.Stage == "ESTABLISHED",
                    Reason = job.PivotType == "TOP" ? "NEXT_DAY_TOP_PRICE_BREAK" : "NEXT_DAY_BOTTOM_PRICE_BREAK",
                    job.EventId
                }, transaction, cancellationToken: cancellationToken));

            if (shouldCorrect)
            {
                var lifecycleReason = current.IsActive && current.Stage == "ESTABLISHED"
                    ? job.PivotType == "TOP" ? "NEXT_DAY_TOP_PRICE_BREAK" : "NEXT_DAY_BOTTOM_PRICE_BREAK"
                    : "NEXT_DAY_VALIDATION_RECONCILED";
                var lifecycleKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"next-day|{job.EventId}|{validationTradingDate:yyyy-MM-dd}|{evaluation.SourceInputHash}"))).ToLowerInvariant();
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO pair_trend_live_lifecycle(
                        event_id,lifecycle_key,symbol,from_stage,to_stage,occurred_at,
                        trigger_frequency,trigger_price,reason,source_row_hash,should_notify)
                    VALUES(
                        @EventId,@LifecycleKey,@Symbol,@FromStage,'INVALIDATED',@OccurredAt,
                        '5m',@TriggerPrice,@Reason,@SourceRowHash,FALSE)
                    ON DUPLICATE KEY UPDATE lifecycle_key=lifecycle_key;
                    """, new
                    {
                        job.EventId,
                        LifecycleKey = lifecycleKey,
                        job.Symbol,
                        FromStage = current.Stage,
                        OccurredAt = evaluation.BreachedAt,
                        TriggerPrice = evaluation.BreachPrice,
                        Reason = lifecycleReason,
                        SourceRowHash = evaluation.SourceInputHash
                    }, transaction, cancellationToken: cancellationToken));
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_next_day_validation
            SET status=@Status,observed_extreme_price=@ObservedExtremePrice,
                breached_at=@BreachedAt,breach_price=@BreachPrice,
                source_input_hash=@SourceInputHash,bar_count=@BarCount,
                verified_missing_count=@VerifiedMissingCount,last_error=NULL,
                lease_token=NULL,lease_owner=NULL,lease_expires_at=NULL,
                completed_at=IF(@Completed,UTC_TIMESTAMP(6),NULL)
            WHERE id=@ValidationId AND (
                (@Realtime=TRUE AND status IN ('PENDING','MONITORING')) OR
                (@Realtime=FALSE AND status='LEASED'));
            """, new
            {
                evaluation.Status,
                evaluation.ObservedExtremePrice,
                evaluation.BreachedAt,
                evaluation.BreachPrice,
                evaluation.SourceInputHash,
                evaluation.BarCount,
                evaluation.VerifiedMissingCount,
                job.ValidationId,
                Realtime = realtime,
                Completed = evaluation.Status != "MONITORING"
            }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private static DateTime[] ValidateSparseProof(
        NextDayValidationSparseProof proof,
        IReadOnlyCollection<NextDayValidationFiveMinuteBar> bars,
        DateOnly validationDate)
    {
        var requiredConfirmations = proof.MissingEobs.Count == 0 ? 1 : SparseConfirmations;
        if (proof.Confirmations != requiredConfirmations)
            throw new InvalidOperationException(
                $"{proof.Symbol} 无成交证明确认次数应为 {requiredConfirmations}。" );
        var expected = ExpectedEobs(validationDate);
        var received = bars.Select(static item => item.Eob).ToHashSet();
        var missing = proof.MissingEobs.ToArray();
        if (missing.Distinct().Count() != missing.Length || missing.Any(item => !expected.Contains(item)))
            throw new InvalidOperationException($"{proof.Symbol} 无成交证明包含重复或计划外EOB。" );
        if (received.Intersect(missing).Any() || !received.Union(missing).ToHashSet().SetEquals(expected))
            throw new InvalidOperationException($"{proof.Symbol} K线与无成交证明未精确覆盖48个窗口。" );
        return missing;
    }

    public static HashSet<DateTime> ExpectedEobs(DateOnly date)
    {
        var result = new HashSet<DateTime>();
        var value = date.ToDateTime(new TimeOnly(9, 35));
        var morningEnd = date.ToDateTime(new TimeOnly(11, 30));
        while (value <= morningEnd) { result.Add(value); value = value.AddMinutes(5); }
        value = date.ToDateTime(new TimeOnly(13, 5));
        var afternoonEnd = date.ToDateTime(new TimeOnly(15, 0));
        while (value <= afternoonEnd) { result.Add(value); value = value.AddMinutes(5); }
        return result;
    }

    private static void ValidateBar(NextDayValidationFiveMinuteBar bar, DateOnly date)
    {
        if (bar.Eob.Date != date.ToDateTime(TimeOnly.MinValue).Date ||
            !ExpectedEobs(date).Contains(bar.Eob))
            throw new ArgumentException($"{bar.Symbol} 包含计划外5分钟EOB {bar.Eob:O}。" );
        if (bar.Bob >= bar.Eob || bar.HighPrice < bar.LowPrice ||
            bar.OpenPrice > bar.HighPrice || bar.OpenPrice < bar.LowPrice ||
            bar.ClosePrice > bar.HighPrice || bar.ClosePrice < bar.LowPrice ||
            bar.Volume < 0 || bar.Amount < 0 || bar.SourceRowHash.Length != 64)
            throw new ArgumentException($"{bar.Symbol}/{bar.Eob:O} 5分钟K线字段无效。" );
    }

    private async Task RefreshRunAsync(long runId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await RefreshRunAsync(connection, transaction, runId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RefreshRunAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        long runId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_next_day_validation_run run
            JOIN (
                SELECT run_id,COUNT(*) total_count,
                    SUM(status IN ('PASSED','INVALIDATED','NO_TRADE','NOT_APPLICABLE')) completed_count,
                    SUM(status='INVALIDATED') invalidated_count,
                    SUM(status='PASSED') passed_count,SUM(status='NO_TRADE') no_trade_count,
                    SUM(status='NOT_APPLICABLE') not_applicable_count,
                    SUM(status='FAILED') failed_count,
                    SUM(status IN ('PENDING','MONITORING','RETRY','LEASED')) remaining_count
                FROM pair_trend_next_day_validation WHERE run_id=@RunId GROUP BY run_id
            ) summary ON summary.run_id=run.id
            SET run.total_count=summary.total_count,run.completed_count=summary.completed_count,
                run.invalidated_count=summary.invalidated_count,run.passed_count=summary.passed_count,
                run.no_trade_count=summary.no_trade_count,
                run.not_applicable_count=summary.not_applicable_count,run.failed_count=summary.failed_count,
                run.status=IF(summary.remaining_count=0,
                    IF(summary.failed_count=0,'COMPLETED','COMPLETED_WITH_ERRORS'),run.status),
                run.completed_at=IF(summary.remaining_count=0,UTC_TIMESTAMP(6),NULL)
            WHERE run.id=@RunId;

            UPDATE pair_trend_next_day_validation_run
            SET status='COMPLETED',completed_at=UTC_TIMESTAMP(6)
            WHERE id=@RunId AND total_count=0;
            """, new { RunId = runId }, transaction, cancellationToken: cancellationToken));
    }

    private sealed class LeaseSession(
        long runId,
        bool applyChanges,
        DateOnly validationTradingDate,
        DateTime expiresAt,
        IReadOnlyCollection<JobRow> jobs)
    {
        public object Gate { get; } = new();
        public long RunId { get; } = runId;
        public bool ApplyChanges { get; } = applyChanges;
        public DateOnly ValidationTradingDate { get; } = validationTradingDate;
        public DateTime ExpiresAt { get; set; } = expiresAt;
        public IReadOnlyCollection<JobRow> Jobs { get; } = jobs;
        public HashSet<string> Symbols { get; } = jobs.Select(static item => item.Symbol).ToHashSet(StringComparer.Ordinal);
        public Dictionary<(string Symbol, DateTime Eob), NextDayValidationFiveMinuteBar> Bars { get; } = [];
    }

    private sealed class SeedEventRow
    {
        public long EventId { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public string PivotType { get; init; } = string.Empty;
        public decimal PairPrice { get; init; }
        public DateTime EstablishedTradingDate { get; init; }
        public DateTime ValidationTradingDate { get; init; }
        public DateTime? InvalidatedAt { get; init; }
    }

    private sealed class JobRow
    {
        public long ValidationId { get; init; }
        public long EventId { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public string PivotType { get; init; } = string.Empty;
        public decimal PairPrice { get; init; }
        public DateTime EstablishedTradingDate { get; init; }
        public DateTime ValidationTradingDate { get; init; }
        public int AttemptCount { get; init; }
    }

    private sealed class EventStateRow
    {
        public long EventId { get; init; }
        public string EventKey { get; init; } = string.Empty;
        public string Stage { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public DateTime? InvalidatedAt { get; init; }
        public decimal? InvalidatedPrice { get; init; }
        public string? InvalidationReason { get; init; }
        public DateTime? LastTransitionAt { get; init; }
        public int EventRevision { get; init; }
        public string ContentHash { get; init; } = string.Empty;
        public string SummaryJson { get; init; } = "{}";
        public string PivotType { get; init; } = string.Empty;
        public decimal PairPrice { get; init; }
        public DateTime? EstablishedAt { get; init; }
    }

    private sealed class RunLeaseRow
    {
        public long RunId { get; init; }
        public string RunMode { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public bool ApplyChanges { get; init; }
    }

    private sealed class RunRow
    {
        public long RunId { get; init; }
        public string Status { get; init; } = string.Empty;
        public DateTime DateFrom { get; init; }
        public DateTime DateTo { get; init; }
        public bool ApplyChanges { get; init; }
        public int Total { get; init; }
        public int Completed { get; init; }
        public int Invalidated { get; init; }
        public int Passed { get; init; }
        public int NoTrade { get; init; }
        public int NotApplicable { get; init; }
        public int Failed { get; init; }
        public string? LastError { get; init; }
        public NextDayValidationRunResponse ToResponse() => new(
            RunId, Status, DateOnly.FromDateTime(DateFrom), DateOnly.FromDateTime(DateTo),
            ApplyChanges, Total, Completed, Invalidated, Passed, NoTrade, NotApplicable, Failed, LastError);
    }
}
