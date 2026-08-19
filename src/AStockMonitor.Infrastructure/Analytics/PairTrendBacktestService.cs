using System.Security.Cryptography;
using System.Data;
using System.Text;
using System.Text.Json;
using AStockMonitor.Application.Analytics;
using AStockMonitor.Domain.Analytics;
using AStockMonitor.Infrastructure.Persistence;
using Dapper;
using MySqlConnector;

namespace AStockMonitor.Infrastructure.Analytics;

/// <summary>
/// 从 MySQL 官方四周期 K 线执行 pair-trend-v3，并以运行、股票检查点、事件、命中和生命周期幂等落库。
/// </summary>
public sealed class PairTrendBacktestService(
    IMySqlConnectionFactory connectionFactory) : IPairTrendBacktestService
{
    private static readonly HashSet<string> AllowedFrequencies =
        new(StringComparer.OrdinalIgnoreCase) { "5m", "30m", "60m", "1d" };

    private readonly PairTrendOptions _options = new();

    /// <summary>执行新回测、从股票级检查点续跑，或直接返回已完成的同参数运行。</summary>
    public async Task<PairTrendBacktestResult> RunAsync(
        PairTrendBacktestRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var frequencies = request.Frequencies
            .Select(PairTrendAnalyzer.NormalizeFrequency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(FrequencyRank)
            .ToArray();
        var parametersJson = JsonSerializer.Serialize(_options);
        var runKey = BuildRunKey(
            request.DateFrom,
            request.DateTo,
            frequencies,
            request.SymbolLimit,
            request.Symbols,
            request.RunMode,
            request.DataSource,
            parametersJson);
        // run_key 覆盖日期、周期、股票范围、运行类型、数据源和算法参数。
        // 普通复跑直接复用完整结果；Force 才会在同一 RunId 下替换结果。
        var existing = await GetRunByKeyAsync(runKey, cancellationToken);
        if (existing is not null &&
            existing.Status.Equals("complete", StringComparison.OrdinalIgnoreCase) &&
            !request.Force)
        {
            return existing.ToResult();
        }

        var runId = await BeginOrResumeRunAsync(
            runKey,
            request,
            frequencies,
            parametersJson,
            request.Force,
            cancellationToken);
        var symbols = await LoadSymbolsAsync(request, cancellationToken);
        await EnsureSymbolCheckpointsAsync(runId, symbols, cancellationToken);
        var pending = await LoadPendingSymbolsAsync(runId, cancellationToken);
        var engine = new PairTrendV3Engine(_options);
        var processedSymbols = 0;

        // 六路受控并发；股票仍是最小事务和恢复单元，单股失败不会影响其他分区。
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 6,
                CancellationToken = cancellationToken
            },
            async (symbol, symbolCancellationToken) =>
        {
            await MarkSymbolRunningAsync(runId, symbol.Symbol, symbolCancellationToken);
            try
            {
                var barsByFrequency = new Dictionary<string, IReadOnlyList<PairTrendBar>>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var frequency in frequencies)
                {
                    var bars = await LoadBarsAsync(
                        symbol.Symbol,
                        frequency,
                        request.DateFrom,
                        request.DateTo,
                        symbolCancellationToken);
                    barsByFrequency[frequency] = bars;
                }

                var result = engine.Replay(
                    symbol.Symbol, symbol.Name, barsByFrequency,
                    request.DateFrom, request.DateTo);
                await SaveSymbolResultWithRetryAsync(
                    runId,
                    result,
                    symbolCancellationToken);
                var progress = Interlocked.Increment(ref processedSymbols);
                if (progress % 100 == 0 || progress == pending.Count)
                    Console.WriteLine($"pair-trend-v3 progress {progress}/{pending.Count}; latest={symbol.Symbol}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await MarkSymbolFailedAsync(
                    runId,
                    symbol.Symbol,
                    exception.Message,
                    symbolCancellationToken);
                Console.Error.WriteLine($"pair-trend failed {symbol.Symbol}: {exception.Message}");
                Interlocked.Increment(ref processedSymbols);
            }
        });

        await FinalizeRunAsync(runId, cancellationToken);
        var completed = await GetRunByIdAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException($"Backtest run {runId} disappeared");
        return completed.ToResult();
    }

    /// <summary>在事务内创建或恢复运行；强制模式会清除该运行的旧事件和检查点。</summary>
    private async Task<long> BeginOrResumeRunAsync(
        string runKey,
        PairTrendBacktestRequest request,
        IReadOnlyCollection<string> frequencies,
        string parametersJson,
        bool force,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO pair_trend_backtest_run
                (run_key, algorithm_version, run_mode, data_source, notes,
                 date_from, date_to, frequencies, parameters_json, status,
                 started_at, finished_at, error_message)
            VALUES
                (@RunKey, @AlgorithmVersion, @RunMode, @DataSource, @Notes,
                 @DateFrom, @DateTo, @Frequencies, @ParametersJson, 'running',
                 CURRENT_TIMESTAMP(6), NULL, NULL)
            ON DUPLICATE KEY UPDATE
                id=LAST_INSERT_ID(id), status='running', finished_at=NULL,
                run_mode=VALUES(run_mode), data_source=VALUES(data_source),
                notes=VALUES(notes), error_message=NULL,
                updated_at=CURRENT_TIMESTAMP(6);
            """,
            new
            {
                RunKey = runKey,
                AlgorithmVersion = _options.AlgorithmVersion,
                RunMode = request.RunMode.Trim().ToLowerInvariant(),
                DataSource = request.DataSource.Trim().ToLowerInvariant(),
                request.Notes,
                DateFrom = request.DateFrom.ToDateTime(TimeOnly.MinValue),
                DateTo = request.DateTo.ToDateTime(TimeOnly.MinValue),
                Frequencies = string.Join(',', frequencies),
                ParametersJson = parametersJson
            },
            transaction,
            cancellationToken: cancellationToken));
        var runId = await connection.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT id FROM pair_trend_backtest_run WHERE run_key=@RunKey;",
            new { RunKey = runKey },
            transaction,
            cancellationToken: cancellationToken));

        if (force)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM pair_trend_event WHERE run_id=@RunId;",
                new { RunId = runId },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM pair_trend_backtest_symbol WHERE run_id=@RunId;",
                new { RunId = runId },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE pair_trend_backtest_run
                SET requested_symbols=0, completed_symbols=0, failed_symbols=0,
                    bars_processed=0, hits_detected=0, events_written=0,
                    started_at=CURRENT_TIMESTAMP(6)
                WHERE id=@RunId;
                """,
                new { RunId = runId },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return runId;
    }

    /// <summary>
    /// 加载回测股票池。正式运行仅允许沪深代码；acceptance 模式才允许 TEST 股票。
    /// </summary>
    private async Task<IReadOnlyList<SymbolRow>> LoadSymbolsAsync(
        PairTrendBacktestRequest request,
        CancellationToken cancellationToken)
    {
        // The point-in-time universe is the authority for replay eligibility.
        // Building candidates by UNION-scanning every K-line table forced a
        // DISTINCT over tens of millions of rows before LIMIT could apply and
        // made even a 20-symbol acceptance run time out.  This query touches
        // only the compact daily universe and uses its date/eligibility index.
        var sql = new StringBuilder(
            """
            SELECT s.symbol AS Symbol, MAX(COALESCE(i.name, s.name)) AS Name
            FROM instrument_daily_status s
            LEFT JOIN instrument i ON i.symbol=s.symbol
            WHERE s.trading_date BETWEEN @DateFrom AND @DateTo
              AND s.is_eligible=TRUE
            """);
        // 防止验收或其他来源的测试证券意外进入正式市场回测统计。
        if (!request.RunMode.Equals("acceptance", StringComparison.OrdinalIgnoreCase))
        {
            sql.AppendLine(" AND (s.symbol LIKE 'SHSE.%' OR s.symbol LIKE 'SZSE.%')");
        }

        if (request.Symbols is { Count: > 0 })
        {
            sql.AppendLine(" AND s.symbol IN @Symbols");
        }

        sql.AppendLine(" GROUP BY s.symbol ORDER BY s.symbol");
        if (request.SymbolLimit is not null)
        {
            sql.Append(" LIMIT @SymbolLimit");
        }

        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<SymbolRow>(new CommandDefinition(
            sql.ToString(),
            new
            {
                DateFrom = request.DateFrom.ToDateTime(TimeOnly.MinValue),
                DateTo = request.DateTo.ToDateTime(TimeOnly.MinValue),
                request.SymbolLimit,
                Symbols = request.Symbols?
                    .Select(static item => item.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            },
            cancellationToken: cancellationToken));
        return rows.AsList();
    }

    private async Task EnsureSymbolCheckpointsAsync(
        long runId,
        IReadOnlyList<SymbolRow> symbols,
        CancellationToken cancellationToken)
    {
        if (symbols.Count == 0)
        {
            await using var emptyConnection = connectionFactory.Create();
            await emptyConnection.ExecuteAsync(new CommandDefinition(
                "UPDATE pair_trend_backtest_run SET requested_symbols=0 WHERE id=@RunId;",
                new { RunId = runId },
                cancellationToken: cancellationToken));
            return;
        }

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO pair_trend_backtest_symbol (run_id, symbol, status)
            VALUES (@RunId, @Symbol, 'pending')
            ON DUPLICATE KEY UPDATE symbol=VALUES(symbol);
            """,
            symbols.Select(item => new { RunId = runId, item.Symbol }),
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_backtest_run
            SET requested_symbols=(
                SELECT COUNT(*) FROM pair_trend_backtest_symbol WHERE run_id=@RunId
            )
            WHERE id=@RunId;
            """,
            new { RunId = runId },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SymbolRow>> LoadPendingSymbolsAsync(
        long runId,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<SymbolRow>(new CommandDefinition(
            """
            SELECT s.symbol AS Symbol, i.name AS Name
            FROM pair_trend_backtest_symbol s
            LEFT JOIN instrument i ON i.symbol=s.symbol
            WHERE s.run_id=@RunId AND s.status <> 'complete'
            ORDER BY s.symbol;
            """,
            new { RunId = runId },
            cancellationToken: cancellationToken));
        return rows.AsList();
    }

    /// <summary>按周期读取回测区间内通过质量检查的东方掘金官方 K 线。</summary>
    private async Task<IReadOnlyList<PairTrendBar>> LoadBarsAsync(
        string symbol,
        string frequency,
        DateOnly dateFrom,
        DateOnly dateTo,
        CancellationToken cancellationToken)
    {
        var (table, frequencyFilter) = frequency switch
        {
            "5m" => ("kline_bar_5m", string.Empty),
            "1d" => ("kline_bar_daily", string.Empty),
            "30m" or "60m" => (
                "kline_bar_agg",
                "AND frequency=@Frequency AND component_count=expected_component_count"),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency))
        };
        var sql = $"""
            SELECT symbol AS Symbol, @Frequency AS Frequency,
                   trading_date AS TradingDate, bob AS Bob, eob AS Eob,
                   open_price AS OpenPrice, high_price AS HighPrice,
                   low_price AS LowPrice, close_price AS ClosePrice,
                   pre_close AS PreClose, CAST(volume AS SIGNED) AS Volume,
                   amount AS Amount,
                   row_hash AS SourceRowHash
            FROM {table}
            WHERE symbol=@Symbol
              AND trading_date BETWEEN @DateFrom AND @DateTo
              AND official_confirmed=TRUE
              AND source_priority>=300
              AND quality_status='passed'
              {frequencyFilter}
            ORDER BY eob;
            """;
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<PairTrendBar>(new CommandDefinition(
            sql,
            new
            {
                Symbol = symbol,
                Frequency = frequency,
                DateFrom = dateFrom.ToDateTime(TimeOnly.MinValue),
                DateTo = dateTo.ToDateTime(TimeOnly.MinValue)
            },
            cancellationToken: cancellationToken));
        return rows.AsList();
    }

    /// <summary>
    /// 在单个事务中替换一只股票的事件和命中，并将股票检查点标记为完成。
    /// </summary>
    private async Task SaveSymbolResultWithRetryAsync(
        long runId,
        PairTrendSymbolResult result,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await SaveSymbolResultAsync(runId, result, cancellationToken);
                return;
            }
            catch (MySqlException exception) when (exception.Number == 1213 && attempt < 6)
            {
                // InnoDB 在并发二级索引页分裂时允许选择任一事务回滚；
                // 单股写入是幂等替换，短退避后重试不会产生重复结果。
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt * attempt), cancellationToken);
            }
        }
    }

    private async Task SaveSymbolResultAsync(
        long runId,
        PairTrendSymbolResult result,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        // 事件是父表，命中通过外键级联删除；替换后不会保留半新半旧的数据。
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM pair_trend_event WHERE run_id=@RunId AND symbol=@Symbol;",
            new { RunId = runId, result.Symbol },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var pairEvent in result.Events)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                InsertEventSql,
                ToEventRow(runId, pairEvent),
                transaction,
                cancellationToken: cancellationToken));
            var eventId = await connection.QuerySingleAsync<long>(new CommandDefinition(
                "SELECT LAST_INSERT_ID();",
                transaction: transaction,
                cancellationToken: cancellationToken));
            if (pairEvent.Hits.Count > 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    InsertHitSql,
                    pairEvent.Hits.Select(item => ToHitRow(runId, eventId, item)),
                    transaction,
                    cancellationToken: cancellationToken));
            }
            if (pairEvent.Lifecycles is { Count: > 0 })
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    InsertLifecycleSql,
                    pairEvent.Lifecycles.Select(item => ToLifecycleRow(
                        runId, eventId, pairEvent.Symbol, item)),
                    transaction,
                    cancellationToken: cancellationToken));
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_backtest_symbol
            SET status='complete', bars_processed=@BarsProcessed,
                hits_detected=@HitsDetected, events_written=@EventsWritten,
                error_message=NULL, finished_at=CURRENT_TIMESTAMP(6)
            WHERE run_id=@RunId AND symbol=@Symbol;
            """,
            new
            {
                RunId = runId,
                result.Symbol,
                result.BarsProcessed,
                result.HitsDetected,
                EventsWritten = result.Events.Count
            },
            transaction,
            cancellationToken: cancellationToken));
        // 不在股票事务内更新共享 Run 行；六路并发会因此形成热点锁和死锁。
        // FinalizeRunAsync 在全部分区完成后从股票检查点统一计算精确汇总。
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task MarkSymbolRunningAsync(
        long runId,
        string symbol,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_backtest_symbol
            SET status='running', error_message=NULL,
                started_at=COALESCE(started_at, CURRENT_TIMESTAMP(6)), finished_at=NULL
            WHERE run_id=@RunId AND symbol=@Symbol;
            """,
            new { RunId = runId, Symbol = symbol },
            cancellationToken: cancellationToken));
    }

    private async Task MarkSymbolFailedAsync(
        long runId,
        string symbol,
        string error,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_backtest_symbol
            SET status='failed', error_message=@Error,
                finished_at=CURRENT_TIMESTAMP(6)
            WHERE run_id=@RunId AND symbol=@Symbol;
            """,
            new { RunId = runId, Symbol = symbol, Error = error[..Math.Min(2000, error.Length)] },
            cancellationToken: cancellationToken));
    }

    private async Task FinalizeRunAsync(long runId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE pair_trend_backtest_run r
            SET
                completed_symbols=(
                    SELECT COUNT(*) FROM pair_trend_backtest_symbol s
                    WHERE s.run_id=r.id AND s.status='complete'
                ),
                failed_symbols=(
                    SELECT COUNT(*) FROM pair_trend_backtest_symbol s
                    WHERE s.run_id=r.id AND s.status='failed'
                ),
                bars_processed=COALESCE((
                    SELECT SUM(s.bars_processed) FROM pair_trend_backtest_symbol s
                    WHERE s.run_id=r.id AND s.status='complete'
                ), 0),
                hits_detected=COALESCE((
                    SELECT SUM(s.hits_detected) FROM pair_trend_backtest_symbol s
                    WHERE s.run_id=r.id AND s.status='complete'
                ), 0),
                events_written=COALESCE((
                    SELECT SUM(s.events_written) FROM pair_trend_backtest_symbol s
                    WHERE s.run_id=r.id AND s.status='complete'
                ), 0),
                status=CASE
                    WHEN EXISTS (
                        SELECT 1 FROM pair_trend_backtest_symbol s
                        WHERE s.run_id=r.id AND s.status='failed'
                    ) THEN 'partial'
                    ELSE 'complete'
                END,
                finished_at=CURRENT_TIMESTAMP(6)
            WHERE r.id=@RunId;
            """,
            new { RunId = runId },
            cancellationToken: cancellationToken));
    }

    private async Task<RunRow?> GetRunByKeyAsync(
        string runKey,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            SelectRunSql + " WHERE run_key=@RunKey;",
            new { RunKey = runKey },
            cancellationToken: cancellationToken));
    }

    private async Task<RunRow?> GetRunByIdAsync(
        long runId,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            SelectRunSql + " WHERE id=@RunId;",
            new { RunId = runId },
            cancellationToken: cancellationToken));
    }

    private object ToEventRow(long runId, PairTrendEventResult item) => new
    {
        RunId = runId,
        item.EventKey,
        item.Symbol,
        item.SymbolName,
        PivotType = ToDatabase(item.PivotType),
        Status = ToDatabase(item.Status),
        item.FirstSeenAt,
        item.LastSeenAt,
        item.ConfirmedAt,
        item.LatestPairPrice,
        item.LatestPairCode,
        LatestPairKind = ToDatabase(item.LatestPairKind),
        item.TimeframeMask,
        item.Frequencies,
        item.StrongestFrequency,
        item.ConfluenceCount,
        item.TotalHitCount,
        item.ConfirmedHitCount,
        item.InvalidatedHitCount,
        item.PendingHitCount,
        item.Round00HitCount,
        item.DoubleDigitHitCount,
        item.Score,
        item.MaxTrendStrength,
        item.AlgorithmVersion,
        item.PriceTicks,
        Stage = ToDatabase(item.Stage),
        item.Generation,
        item.IsActive,
        DiscoveredAt = item.FirstSeenAt,
        item.ObservedAt,
        item.FocusedAt,
        item.EstablishedAt,
        item.InvalidatedAt,
        item.InvalidatedPrice,
        item.InvalidationReason,
        item.RootFiveMinuteBob,
        item.RootFiveMinuteEob,
        LastTransitionAt = item.Lifecycles?.Max(static value => value.OccurredAt)
            ?? item.LastSeenAt,
        SummaryJson = JsonSerializer.Serialize(new
        {
            item.Symbol,
            pivotType = ToDatabase(item.PivotType),
            status = ToDatabase(item.Status),
            item.Frequencies,
            item.ConfluenceCount,
            item.TotalHitCount,
            item.ConfirmedHitCount,
            item.InvalidatedHitCount,
            item.PendingHitCount,
            item.Round00HitCount,
            item.DoubleDigitHitCount,
            item.Score,
            stage = ToDatabase(item.Stage),
            item.PriceTicks,
            item.Generation,
            item.IsActive,
            item.ObservedAt,
            item.FocusedAt,
            item.EstablishedAt,
            item.InvalidatedAt,
            item.InvalidatedPrice,
            item.InvalidationReason
        })
    };

    private object ToHitRow(long runId, long eventId, PairTrendHitResult item) => new
    {
        RunId = runId,
        EventId = eventId,
        item.HitKey,
        item.Symbol,
        item.Frequency,
        item.TradingDate,
        item.Bob,
        item.Eob,
        item.ObservedAt,
        item.ConfirmedAt,
        PivotType = ToDatabase(item.PivotType),
        Status = ToDatabase(item.Status),
        item.PairPrice,
        item.PriceTicks,
        item.PairCode,
        PairKind = ToDatabase(item.PairKind),
        item.HitField,
        TrendDirection = ToDatabase(item.TrendDirection),
        item.TrendStrength,
        item.Ema20,
        item.Ema60,
        item.Atr14,
        item.PreviousClose,
        item.OpenPrice,
        item.HighPrice,
        item.LowPrice,
        item.ClosePrice,
        item.Volume,
        item.Amount,
        item.IsRollingExtreme,
        item.VolumePercentile,
        item.WickRatio,
        item.ReversalAtr,
        item.Score,
        item.ConfirmationReason,
        item.SourceRowHash,
        item.AlgorithmVersion,
        Stage = ToDatabase(item.Stage),
        item.IsPromotion,
        DetailsJson = JsonSerializer.Serialize(new
        {
            item.PriceTicks,
            stage = ToDatabase(item.Stage),
            item.IsPromotion,
            _options.IncludeRound00
        })
    };

    private static object ToLifecycleRow(
        long runId,
        long eventId,
        string symbol,
        PairTrendLifecycleResult item) => new
    {
        RunId = runId,
        EventId = eventId,
        item.LifecycleKey,
        Symbol = symbol,
        FromStage = item.FromStage is null ? null : ToDatabase(item.FromStage.Value),
        ToStage = ToDatabase(item.ToStage),
        item.OccurredAt,
        item.TriggerFrequency,
        item.TriggerPrice,
        item.Reason,
        item.SourceRowHash,
        item.ShouldNotify
    };

    private static string ToDatabase(PairPivotType value) => value == PairPivotType.Top ? "TOP" : "BOTTOM";
    private static string ToDatabase(PairEventStatus value) => value.ToString().ToUpperInvariant();
    private static string ToDatabase(PairHitStatus value) => value.ToString().ToUpperInvariant();
    private static string ToDatabase(PairTrendDirection value) => value.ToString().ToUpperInvariant();
    private static string ToDatabase(PairPriceKind value) => value == PairPriceKind.Round00 ? "ROUND_00" : "DOUBLE_DIGIT";
    private static string ToDatabase(PairTrendStage value) => value switch
    {
        PairTrendStage.Discovered => "DISCOVERED",
        PairTrendStage.Observing => "OBSERVING",
        PairTrendStage.Focus => "FOCUS",
        PairTrendStage.Established => "ESTABLISHED",
        PairTrendStage.Invalidated => "INVALIDATED",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    /// <summary>生成跨进程稳定的运行幂等键，避免同参数重复产生正式记录。</summary>
    private static string BuildRunKey(
        DateOnly dateFrom,
        DateOnly dateTo,
        IReadOnlyCollection<string> frequencies,
        int? symbolLimit,
        IReadOnlyList<string>? symbols,
        string runMode,
        string dataSource,
        string parametersJson)
    {
        var symbolIdentity = symbols is { Count: > 0 }
            ? string.Join(',', symbols.Select(static item => item.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
            : "all";
        var identity = $"{dateFrom:yyyy-MM-dd}|{dateTo:yyyy-MM-dd}|{string.Join(',', frequencies)}|symbols={symbolIdentity}|limit={symbolLimit?.ToString() ?? "all"}|mode={runMode.Trim().ToLowerInvariant()}|source={dataSource.Trim().ToLowerInvariant()}|{parametersJson}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return $"{PairTrendOptions.CurrentAlgorithmVersion}:{dateFrom:yyyyMMdd}:{dateTo:yyyyMMdd}:{hash}";
    }

    private static int FrequencyRank(string frequency) => frequency switch
    {
        "5m" => 1,
        "30m" => 2,
        "60m" => 3,
        "1d" => 4,
        _ => 0
    };

    private static void Validate(PairTrendBacktestRequest request)
    {
        if (request.DateFrom > request.DateTo)
        {
            throw new ArgumentException("DateFrom must not be after DateTo");
        }

        if (request.Frequencies.Count == 0 ||
            request.Frequencies.Any(item => !AllowedFrequencies.Contains(
                PairTrendAnalyzer.NormalizeFrequency(item))))
        {
            throw new ArgumentException("Frequencies must be selected from 5m,30m,60m,1d");
        }

        if (request.SymbolLimit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.SymbolLimit));
        }

        if (request.Symbols?.Any(string.IsNullOrWhiteSpace) == true ||
            request.Symbols?.Any(static item => item.Length > 32) == true)
        {
            throw new ArgumentException("Symbols must be non-empty and must not exceed 32 characters");
        }

        if (string.IsNullOrWhiteSpace(request.RunMode) || request.RunMode.Length > 24)
        {
            throw new ArgumentException("RunMode is required and must not exceed 24 characters");
        }

        if (string.IsNullOrWhiteSpace(request.DataSource) || request.DataSource.Length > 64)
        {
            throw new ArgumentException("DataSource is required and must not exceed 64 characters");
        }

        if (request.Notes?.Length > 1000)
        {
            throw new ArgumentException("Notes must not exceed 1000 characters");
        }
    }

    private sealed class SymbolRow
    {
        public string Symbol { get; init; } = string.Empty;
        public string? Name { get; init; }
    }

    private sealed class RunRow
    {
        public long RunId { get; init; }
        public string RunKey { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public int RequestedSymbols { get; init; }
        public int CompletedSymbols { get; init; }
        public int FailedSymbols { get; init; }
        public long BarsProcessed { get; init; }
        public long HitsDetected { get; init; }
        public long EventsWritten { get; init; }

        public PairTrendBacktestResult ToResult() => new(
            RunId,
            RunKey,
            Status,
            RequestedSymbols,
            CompletedSymbols,
            FailedSymbols,
            BarsProcessed,
            HitsDetected,
            EventsWritten);
    }

    private const string SelectRunSql = """
        SELECT id AS RunId, run_key AS RunKey, status AS Status,
               requested_symbols AS RequestedSymbols,
               completed_symbols AS CompletedSymbols,
               failed_symbols AS FailedSymbols,
               bars_processed AS BarsProcessed,
               hits_detected AS HitsDetected,
               events_written AS EventsWritten
        FROM pair_trend_backtest_run
        """;

    private const string InsertEventSql = """
        INSERT INTO pair_trend_event
            (run_id, event_key, symbol, symbol_name, pivot_type, status,
             first_seen_at, last_seen_at, confirmed_at, latest_pair_price,
             price_ticks,latest_pair_code, latest_pair_kind, timeframe_mask, frequencies,
             strongest_frequency, confluence_count, total_hit_count,
             confirmed_hit_count, invalidated_hit_count, pending_hit_count,
             round_00_hit_count, double_digit_hit_count, score,
             max_trend_strength, algorithm_version,stage,generation,is_active,
             discovered_at,observed_at,focused_at,established_at,invalidated_at,
             invalidated_price,invalidation_reason,root_5m_bob,root_5m_eob,
             last_transition_at,summary_json)
        VALUES
            (@RunId, @EventKey, @Symbol, @SymbolName, @PivotType, @Status,
             @FirstSeenAt, @LastSeenAt, @ConfirmedAt, @LatestPairPrice,
             @PriceTicks,@LatestPairCode, @LatestPairKind, @TimeframeMask, @Frequencies,
             @StrongestFrequency, @ConfluenceCount, @TotalHitCount,
             @ConfirmedHitCount, @InvalidatedHitCount, @PendingHitCount,
             @Round00HitCount, @DoubleDigitHitCount, @Score,
             @MaxTrendStrength, @AlgorithmVersion,@Stage,@Generation,@IsActive,
             @DiscoveredAt,@ObservedAt,@FocusedAt,@EstablishedAt,@InvalidatedAt,
             @InvalidatedPrice,@InvalidationReason,@RootFiveMinuteBob,@RootFiveMinuteEob,
             @LastTransitionAt,@SummaryJson);
        """;

    private const string InsertHitSql = """
        INSERT INTO pair_trend_hit
            (run_id, event_id, hit_key, symbol, frequency, trading_date,
             bob, eob, observed_at, confirmed_at, pivot_type, status,
             pair_price, pair_code, pair_kind, hit_field, trend_direction,
             trend_strength, ema20, ema60, atr14, previous_close,
             open_price, high_price, low_price, close_price, volume, amount,
             is_rolling_extreme, volume_percentile, wick_ratio, reversal_atr,
             score, confirmation_reason, source_row_hash, algorithm_version,
             price_ticks,stage,is_promotion,details_json)
        VALUES
            (@RunId, @EventId, @HitKey, @Symbol, @Frequency, @TradingDate,
             @Bob, @Eob, @ObservedAt, @ConfirmedAt, @PivotType, @Status,
             @PairPrice, @PairCode, @PairKind, @HitField, @TrendDirection,
             @TrendStrength, @Ema20, @Ema60, @Atr14, @PreviousClose,
             @OpenPrice, @HighPrice, @LowPrice, @ClosePrice, @Volume, @Amount,
             @IsRollingExtreme, @VolumePercentile, @WickRatio, @ReversalAtr,
             @Score, @ConfirmationReason, @SourceRowHash, @AlgorithmVersion,
             @PriceTicks,@Stage,@IsPromotion,@DetailsJson);
        """;

    private const string InsertLifecycleSql = """
        INSERT INTO pair_trend_lifecycle
            (run_id,event_id,lifecycle_key,symbol,from_stage,to_stage,occurred_at,
             trigger_frequency,trigger_price,reason,source_row_hash,should_notify)
        VALUES
            (@RunId,@EventId,@LifecycleKey,@Symbol,@FromStage,@ToStage,@OccurredAt,
             @TriggerFrequency,@TriggerPrice,@Reason,@SourceRowHash,@ShouldNotify);
        """;
}
