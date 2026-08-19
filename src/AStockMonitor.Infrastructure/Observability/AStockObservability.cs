using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AStockMonitor.Infrastructure.Observability;

/// <summary>Configures the shared OTLP metrics, logs and traces pipeline.</summary>
public static class AStockObservability
{
    public const string MeterName = "AStockMonitor.Operations";
    public const string ActivitySourceName = "AStockMonitor.Operations";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    private static readonly Counter<long> ItemsProcessed =
        Meter.CreateCounter<long>("astock.pipeline.items.processed");
    private static readonly Counter<long> Failures =
        Meter.CreateCounter<long>("astock.pipeline.failures");
    private static readonly Counter<long> BarEvents =
        Meter.CreateCounter<long>("astock.bar.events");
    private static readonly Counter<long> SignalRMessages =
        Meter.CreateCounter<long>("astock.signalr.messages");
    private static readonly Counter<long> IngestMessages =
        Meter.CreateCounter<long>("astock.ingest.messages");
    private static readonly Histogram<double> IngestLag =
        Meter.CreateHistogram<double>("astock.ingest.lag", "ms");
    private static readonly Histogram<double> BatchDuration =
        Meter.CreateHistogram<double>("astock.pipeline.batch.duration", "ms");
    private static readonly Counter<long> StrategyEvaluated =
        Meter.CreateCounter<long>("astock.strategy.symbols.evaluated");
    private static readonly Counter<long> StrategyQualified =
        Meter.CreateCounter<long>("astock.strategy.signals.qualified");
    private static readonly Histogram<double> StrategyScanDuration =
        Meter.CreateHistogram<double>("astock.strategy.scan.duration", "ms");
    private static readonly Counter<long> PairTicksScreened =
        Meter.CreateCounter<long>("astock.pair.tick.screened");
    private static readonly Counter<long> PairTickBreaks =
        Meter.CreateCounter<long>("astock.pair.tick.breaks");
    private static readonly Counter<long> PairTransitions =
        Meter.CreateCounter<long>("astock.pair.transitions");
    private static readonly Counter<long> HistoryWatchdogTerminations =
        Meter.CreateCounter<long>("astock.history.watchdog.terminations");
    private static readonly Counter<long> HistoryMetricsPollFailures =
        Meter.CreateCounter<long>("astock.history.metrics.poll.failures");
    private static HistoryPartitionMetricSnapshot _historySnapshot =
        HistoryPartitionMetricSnapshot.Empty;
    private static MarketOperationalMetricSnapshot _marketSnapshot =
        MarketOperationalMetricSnapshot.Empty;
    private static readonly ConcurrentDictionary<string, long> StartedAt = new();
    private static readonly ConcurrentDictionary<string, long> LastSuccessAt = new();
    private static readonly ObservableGauge<long> StartedGauge = Meter.CreateObservableGauge(
        "astock.component.started.time",
        () => StartedAt.Select(static item => new Measurement<long>(
            item.Value, new KeyValuePair<string, object?>("component", item.Key))));
    private static readonly ObservableGauge<long> LastSuccessGauge = Meter.CreateObservableGauge(
        "astock.component.last.success.time",
        () => LastSuccessAt.Select(static item => new Measurement<long>(
            item.Value, new KeyValuePair<string, object?>("component", item.Key))));
    private static readonly ObservableGauge<long> HistoryPartitionsGauge =
        Meter.CreateObservableGauge(
            "astock.history.partitions",
            ObserveHistoryPartitions,
            description: "History download partitions grouped by durable state");
    private static readonly ObservableGauge<long> HistoryWorkersGauge =
        Meter.CreateObservableGauge("astock.history.workers.active",
            () => Volatile.Read(ref _historySnapshot).ActiveWorkers);
    private static readonly ObservableGauge<long> HistoryPendingSymbolsGauge =
        Meter.CreateObservableGauge("astock.history.symbols.pending",
            () => Volatile.Read(ref _historySnapshot).PendingSymbols);
    private static readonly ObservableGauge<double> HistoryBatchProgressGauge =
        Meter.CreateObservableGauge("astock.history.batch.progress",
            () => Volatile.Read(ref _historySnapshot).BatchProgress);
    private static readonly ObservableGauge<long> HistoryRowsWrittenGauge =
        Meter.CreateObservableGauge("astock.history.batch.rows.written",
            () => Volatile.Read(ref _historySnapshot).RowsWritten);
    private static readonly ObservableGauge<double> HistoryRowsRateGauge =
        Meter.CreateObservableGauge("astock.history.rows.per.second",
            () => Volatile.Read(ref _historySnapshot).RowsPerSecond);
    private static readonly ObservableGauge<long> HistoryHeartbeatAgeGauge =
        Meter.CreateObservableGauge("astock.history.heartbeat.age.seconds.max",
            () => Volatile.Read(ref _historySnapshot).MaxHeartbeatAgeSeconds);
    private static readonly ObservableGauge<long> HistoryProgressAgeGauge =
        Meter.CreateObservableGauge("astock.history.progress.age.seconds.max",
            () => Volatile.Read(ref _historySnapshot).MaxProgressAgeSeconds);
    private static readonly ObservableGauge<double> HistoryEtaGauge =
        Meter.CreateObservableGauge("astock.history.batch.eta.seconds",
            () => Volatile.Read(ref _historySnapshot).EtaSeconds);
    private static readonly ObservableGauge<long> HistorySchedulerLeaseGauge =
        Meter.CreateObservableGauge("astock.history.scheduler.lease",
            () => Volatile.Read(ref _historySnapshot).SchedulerLeaseValid ? 1L : 0L);
    private static readonly ObservableGauge<long> CollectorWorkersGauge =
        Meter.CreateObservableGauge("astock.collector.workers.connected",
            () => Volatile.Read(ref _marketSnapshot).ConnectedCollectors);
    private static readonly ObservableGauge<long> CollectorHeartbeatAgeGauge =
        Meter.CreateObservableGauge("astock.collector.heartbeat.age.seconds.max",
            () => Volatile.Read(ref _marketSnapshot).MaxCollectorHeartbeatAgeSeconds);
    private static readonly ObservableGauge<long> CollectorQueueDepthGauge =
        Meter.CreateObservableGauge("astock.collector.queue.depth",
            () => Volatile.Read(ref _marketSnapshot).CollectorQueueDepth);
    private static readonly ObservableGauge<long> CollectorOutboxPendingGauge =
        Meter.CreateObservableGauge("astock.collector.outbox.pending",
            () => Volatile.Read(ref _marketSnapshot).CollectorOutboxPending);
    private static readonly ObservableGauge<long> RecoveryRunsGauge =
        Meter.CreateObservableGauge("astock.recovery.runs.active",
            () => Volatile.Read(ref _marketSnapshot).ActiveRecoveryRuns);
    private static readonly ObservableGauge<long> RecoveryItemsGauge =
        Meter.CreateObservableGauge("astock.recovery.items.pending",
            () => Volatile.Read(ref _marketSnapshot).PendingRecoveryItems);
    private static readonly ObservableGauge<long> RecoveryStaleGauge =
        Meter.CreateObservableGauge("astock.recovery.items.stale",
            () => Volatile.Read(ref _marketSnapshot).StaleRecoveryItems);
    private static readonly ObservableGauge<long> StrategyFailuresGauge =
        Meter.CreateObservableGauge("astock.strategy.runs.failed.recent",
            () => Volatile.Read(ref _marketSnapshot).RecentStrategyFailures);
    private static readonly ObservableGauge<long> BarOutboxPendingGauge =
        Meter.CreateObservableGauge("astock.bar.outbox.pending",
            () => Volatile.Read(ref _marketSnapshot).BarOutboxPending);
    private static readonly ObservableGauge<long> SnapshotPublishedGauge =
        Meter.CreateObservableGauge("astock.market.v4.snapshot.published",
            () => Volatile.Read(ref _marketSnapshot).SnapshotPublished);
    private static readonly ObservableGauge<long> SnapshotStaleGauge =
        Meter.CreateObservableGauge("astock.market.v4.snapshot.stale",
            () => Volatile.Read(ref _marketSnapshot).SnapshotStale);
    private static readonly ObservableGauge<double> SnapshotElapsedGauge =
        Meter.CreateObservableGauge("astock.market.v4.snapshot.elapsed.ms",
            () => Volatile.Read(ref _marketSnapshot).SnapshotElapsedMilliseconds);
    private static readonly ObservableGauge<long> SnapshotAgeGauge =
        Meter.CreateObservableGauge("astock.market.v4.snapshot.age.seconds",
            () => Volatile.Read(ref _marketSnapshot).SnapshotAgeSeconds);
    private static readonly ObservableGauge<long> HotTickDesiredGauge =
        Meter.CreateObservableGauge("astock.market.v4.hot_tick.desired",
            () => Volatile.Read(ref _marketSnapshot).HotTickDesired);
    private static readonly ObservableGauge<long> HotTickWorkersGauge =
        Meter.CreateObservableGauge("astock.market.v4.hot_tick.workers",
            () => Volatile.Read(ref _marketSnapshot).HotTickWorkers);
    private static readonly ObservableGauge<long> HotTickBaseCandidatesGauge =
        Meter.CreateObservableGauge("astock.market.v5.hot_tick.base_candidates",
            () => Volatile.Read(ref _marketSnapshot).HotTickBaseCandidates);
    private static readonly ObservableGauge<long> HotTickIntradayCandidatesGauge =
        Meter.CreateObservableGauge("astock.market.v5.hot_tick.intraday_candidates",
            () => Volatile.Read(ref _marketSnapshot).HotTickIntradayCandidates);
    private static readonly ObservableGauge<long> HotTickOverflowGauge =
        Meter.CreateObservableGauge("astock.market.v5.hot_tick.overflow",
            () => Volatile.Read(ref _marketSnapshot).HotTickOverflow);
    private static readonly ObservableGauge<long> TradingDayGauge =
        Meter.CreateObservableGauge("astock.market.is_trading_day",
            () => Volatile.Read(ref _marketSnapshot).IsTradingDay);

    public static IServiceCollection AddAStockObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        bool includeAspNetCore)
    {
        var endpoint = GetEndpoint(configuration);
        var resource = BuildResource(configuration, serviceName);
        services.AddOpenTelemetry()
            .ConfigureResource(builder => builder.AddService(
                serviceName,
                serviceVersion: "0.1.0",
                serviceInstanceId: $"{Environment.MachineName}-{Environment.ProcessId}"))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(MeterName).AddRuntimeInstrumentation();
                if (includeAspNetCore)
                {
                    metrics.AddAspNetCoreInstrumentation();
                }
                metrics.AddOtlpExporter(options => options.Endpoint = endpoint);
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(ActivitySourceName).AddHttpClientInstrumentation();
                if (includeAspNetCore)
                {
                    tracing.AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                    });
                }
                tracing.AddOtlpExporter(options => options.Endpoint = endpoint);
            });

        _ = resource;
        return services;
    }

    public static ILoggingBuilder AddAStockOpenTelemetry(
        this ILoggingBuilder logging,
        IConfiguration configuration,
        string serviceName)
    {
        var endpoint = GetEndpoint(configuration);
        logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.SetResourceBuilder(BuildResource(configuration, serviceName));
            options.AddOtlpExporter(exporter => exporter.Endpoint = endpoint);
        });
        return logging;
    }

    public static void ComponentStarted(string component)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        StartedAt.TryAdd(component, now);
        LastSuccessAt.TryAdd(component, now);
    }

    public static void RecordPipelineBatch(
        string component,
        string shard,
        long itemCount,
        long failureCount,
        double elapsedMilliseconds)
    {
        var tags = new TagList { { "component", component }, { "shard", shard } };
        ItemsProcessed.Add(itemCount, tags);
        if (failureCount > 0)
        {
            Failures.Add(failureCount, tags);
        }
        BatchDuration.Record(elapsedMilliseconds, tags);
        LastSuccessAt[component] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public static void RecordBarEvents(IEnumerable<(string EventType, string Frequency)> events)
    {
        foreach (var item in events)
        {
            BarEvents.Add(1,
                new KeyValuePair<string, object?>("event_type", item.EventType),
                new KeyValuePair<string, object?>("frequency", item.Frequency));
        }
        LastSuccessAt["realtime-bar-engine"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public static void RecordSignalRMessage() => SignalRMessages.Add(1);

    public static void RecordIngest(string outcome, double lagMilliseconds)
    {
        IngestMessages.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        if (outcome == "accepted")
        {
            IngestLag.Record(Math.Max(0, lagMilliseconds));
            LastSuccessAt["grpc-ingest"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    public static void RecordIngestBatch(
        long accepted,
        long duplicate,
        long expired,
        long rejected,
        double maximumLagMilliseconds)
    {
        if (accepted > 0)
        {
            IngestMessages.Add(accepted,
                new KeyValuePair<string, object?>("outcome", "accepted"));
            IngestLag.Record(Math.Max(0, maximumLagMilliseconds));
            LastSuccessAt["grpc-ingest"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        if (duplicate > 0)
            IngestMessages.Add(duplicate,
                new KeyValuePair<string, object?>("outcome", "duplicate"));
        if (expired > 0)
            IngestMessages.Add(expired,
                new KeyValuePair<string, object?>("outcome", "expired"));
        if (rejected > 0)
            IngestMessages.Add(rejected,
                new KeyValuePair<string, object?>("outcome", "rejected"));
    }

    public static void RecordFailure(string component) => Failures.Add(
        1, new KeyValuePair<string, object?>("component", component));

    public static void RecordStrategyScan(
        string profile, long symbolCount, long qualifiedCount, long failureCount,
        double elapsedMilliseconds)
    {
        var tags = new TagList { { "profile", profile } };
        StrategyEvaluated.Add(symbolCount, tags);
        StrategyQualified.Add(qualifiedCount, tags);
        StrategyScanDuration.Record(elapsedMilliseconds, tags);
        if (failureCount > 0)
        {
            Failures.Add(failureCount,
                new KeyValuePair<string, object?>("component", "strategy-scanner"),
                new KeyValuePair<string, object?>("profile", profile));
        }
        LastSuccessAt[$"strategy-{profile}"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public static void RecordPairTickBatch(long screened, long breaks)
    {
        if (screened > 0) PairTicksScreened.Add(screened);
        if (breaks > 0) PairTickBreaks.Add(breaks);
        LastSuccessAt["pair-trend-tick-invalidation-v3"] =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public static void RecordPairTransition(string fromStage, string toStage, string triggerFrequency)
    {
        PairTransitions.Add(1,
            new KeyValuePair<string, object?>("from_stage", fromStage),
            new KeyValuePair<string, object?>("to_stage", toStage),
            new KeyValuePair<string, object?>("trigger_frequency", triggerFrequency));
        LastSuccessAt["pair-trend-realtime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>Atomically publishes the latest low-cardinality history scheduler snapshot.</summary>
    public static void UpdateHistoryPartitionSnapshot(HistoryPartitionMetricSnapshot snapshot)
    {
        Volatile.Write(ref _historySnapshot, snapshot);
        LastSuccessAt["history-partition-monitor"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>Publishes collector, recovery, strategy and durable outbox health.</summary>
    public static void UpdateMarketOperationalSnapshot(MarketOperationalMetricSnapshot snapshot)
    {
        Volatile.Write(ref _marketSnapshot, snapshot);
        LastSuccessAt["market-operations-monitor"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>Records newly observed watchdog attempt failures.</summary>
    public static void RecordHistoryWatchdogTermination(string reason, long count = 1) =>
        HistoryWatchdogTerminations.Add(count,
            new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Records a database polling failure in the history metrics worker.</summary>
    public static void RecordHistoryMetricsPollFailure() => HistoryMetricsPollFailures.Add(1);

    public static Activity? StartActivity(string name) => ActivitySource.StartActivity(name);

    private static IEnumerable<Measurement<long>> ObserveHistoryPartitions()
    {
        var snapshot = Volatile.Read(ref _historySnapshot);
        foreach (var item in snapshot.PartitionsByStatus)
        {
            yield return new Measurement<long>(item.Value,
                new KeyValuePair<string, object?>("status", item.Key));
        }
    }

    private static Uri GetEndpoint(IConfiguration configuration) => new(
        configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
        ?? configuration["Observability:OtlpEndpoint"]
        ?? "http://127.0.0.1:4317");

    private static ResourceBuilder BuildResource(IConfiguration configuration, string serviceName) =>
        ResourceBuilder.CreateDefault()
            .AddService(
                serviceName,
                serviceVersion: "0.1.0",
                serviceInstanceId: $"{Environment.MachineName}-{Environment.ProcessId}")
            .AddAttributes([
                new KeyValuePair<string, object>(
                    "deployment.environment.name",
                    configuration["DOTNET_ENVIRONMENT"] ?? "Development"),
                new KeyValuePair<string, object>("host.name", Environment.MachineName)
            ]);
}

public sealed record MarketOperationalMetricSnapshot(
    long ConnectedCollectors,
    long MaxCollectorHeartbeatAgeSeconds,
    long CollectorQueueDepth,
    long CollectorOutboxPending,
    long ActiveRecoveryRuns,
    long PendingRecoveryItems,
    long StaleRecoveryItems,
    long RecentStrategyFailures,
    long BarOutboxPending,
    long SnapshotPublished,
    long SnapshotStale,
    double SnapshotElapsedMilliseconds,
    long SnapshotAgeSeconds,
    long HotTickDesired,
    long HotTickWorkers,
    long HotTickBaseCandidates,
    long HotTickIntradayCandidates,
    long HotTickOverflow,
    long IsTradingDay)
{
    public static MarketOperationalMetricSnapshot Empty { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>One in-memory aggregate sample exported through OpenTelemetry.</summary>
public sealed record HistoryPartitionMetricSnapshot(
    IReadOnlyDictionary<string, long> PartitionsByStatus,
    long ActiveWorkers,
    long PendingSymbols,
    double BatchProgress,
    long RowsWritten,
    double RowsPerSecond,
    long MaxHeartbeatAgeSeconds,
    long MaxProgressAgeSeconds,
    double EtaSeconds,
    bool SchedulerLeaseValid)
{
    public static HistoryPartitionMetricSnapshot Empty { get; } = new(
        new Dictionary<string, long>(), 0, 0, 0, 0, 0, 0, 0, 0, false);
}
