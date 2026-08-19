using System.Diagnostics;
using AStockMonitor.Api.Models;
using AStockMonitor.Application.Collection;

namespace AStockMonitor.Api.Services;

public sealed class OperationsStatusService(
    ICollectorOperationsRepository repository,
    CollectorOperationsOptions options,
    PairTrendCollectionSessionStore sessionStore,
    IWebHostEnvironment environment,
    ILogger<OperationsStatusService> logger)
{
    private static readonly DateTime ProcessStartedAt =
        Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public async Task<OperationsStatusResponse> GetAsync(
        string websiteUrl,
        CancellationToken cancellationToken)
    {
        var totalTimer = Stopwatch.StartNew();
        var checkedAt = DateTime.UtcNow;
        CollectorOperationsSnapshot? snapshot = null;
        var databaseTimer = Stopwatch.StartNew();
        try
        {
            snapshot = await repository.GetSnapshotAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "读取采集运维快照失败。");
        }
        databaseTimer.Stop();

        var collection = sessionStore.GetStatus();
        var heartbeatReferenceTime = snapshot?.DatabaseUtcNow ?? checkedAt;
        var expectedProcesses = Math.Clamp(options.ExpectedProcesses, 1, 64);
        var collectors = snapshot?.Collectors
            .Select(item => MapCollector(
                item, heartbeatReferenceTime, options.HeartbeatOfflineSeconds, expectedProcesses))
            .ToArray() ?? Array.Empty<OperationsCollectorInstance>();
        var primary = collectors.FirstOrDefault();
        var collector = primary is null
            ? new OperationsCollectorStatus(
                "offline", null, expectedProcesses, 0, 0, 0, 0, 0, 0,
                snapshot?.ActiveBlacklistCount ?? 0, Array.Empty<OperationsCollectorProcess>())
            : new OperationsCollectorStatus(
                primary.Health,
                primary.LastHeartbeatAt,
                expectedProcesses,
                primary.ProcessesRunning,
                primary.ActiveJobs,
                primary.QueuedJobs,
                primary.SucceededJobs,
                primary.RetryingJobs,
                primary.FailedJobs,
                snapshot?.ActiveBlacklistCount ?? primary.BlacklistedSymbols,
                primary.Processes);

        var websiteTimer = Stopwatch.StartNew();
        var (indexExists, assetCount) = InspectWebsite();
        websiteTimer.Stop();
        var websiteStatus = indexExists ? "healthy" : "unhealthy";
        var databaseStatus = snapshot is null ? "unhealthy" : "healthy";
        var apiStatus = snapshot is null ? "degraded" : "healthy";
        var overallStatus = OperationsHealthPolicy.ResolveOverallStatus(
            apiStatus,
            websiteStatus,
            collector.Status,
            collection.Status,
            snapshot?.ActiveBlacklistCount ?? 0);

        var recentErrors = new List<OperationsRecentError>();
        foreach (var instance in snapshot?.Collectors ?? Array.Empty<CollectorHeartbeatSnapshot>())
        {
            if (!string.IsNullOrWhiteSpace(instance.LastError) && instance.LastErrorAt is { } occurredAt)
                recentErrors.Add(new OperationsRecentError(
                    $"collector:{instance.CollectorId}", instance.LastError, occurredAt));
            foreach (var process in instance.Processes)
            {
                if (!string.IsNullOrWhiteSpace(process.LastError))
                    recentErrors.Add(new OperationsRecentError(
                        $"collector:{instance.CollectorId}/{process.WorkerId}",
                        process.LastError,
                        instance.LastSeenAt));
            }
        }
        if (!string.IsNullOrWhiteSpace(collection.LastError))
            recentErrors.Add(new OperationsRecentError(
                "pair-trend-collection",
                CollectorOperationsReportService.SanitizeError(collection.LastError)
                    ?? "collection error",
                collection.LastErrorAt ?? checkedAt));

        totalTimer.Stop();
        return new OperationsStatusResponse(
            checkedAt,
            overallStatus,
            collector,
            new OperationsApiStatus(
                apiStatus,
                "AStockMonitor.Api",
                typeof(OperationsStatusService).Assembly.GetName().Version?.ToString() ?? "unknown",
                Math.Max(0, (long)(checkedAt - ProcessStartedAt).TotalSeconds),
                Math.Round(totalTimer.Elapsed.TotalMilliseconds, 2),
                collection.LastErrorAt),
            new OperationsWebsiteStatus(
                websiteStatus,
                "AStockMonitor.Web",
                websiteUrl,
                Math.Round(websiteTimer.Elapsed.TotalMilliseconds, 2),
                checkedAt,
                indexExists,
                assetCount),
            collection,
            collectors,
            new OperationsBlacklistStatus(
                snapshot?.ActiveBlacklistCount ?? 0,
                snapshot?.RecentBlacklists.Select(static item => new OperationsBlacklistEntry(
                    item.Symbol,
                    item.FailureCount,
                    item.Reason,
                    DateTime.SpecifyKind(item.BlacklistedAt, DateTimeKind.Utc),
                    DateTime.SpecifyKind(item.ExpiresAt, DateTimeKind.Utc))).ToArray()
                ?? Array.Empty<OperationsBlacklistEntry>()),
            new OperationsDatabaseStatus(
                databaseStatus,
                Math.Round(databaseTimer.Elapsed.TotalMilliseconds, 2)),
            recentErrors
                .OrderByDescending(static item => item.OccurredAt)
                .Take(20)
                .ToArray());
    }

    private static OperationsCollectorInstance MapCollector(
        CollectorHeartbeatSnapshot source,
        DateTime checkedAt,
        int offlineSeconds,
        int configuredProcesses)
    {
        var age = Math.Max(0, (long)(checkedAt - source.LastSeenAt).TotalSeconds);
        var offline = age > Math.Clamp(offlineSeconds, 10, 600);
        var badState = source.Status is "failed" or "stopped" or "offline";
        var expectedMismatch = source.ProcessesExpected != configuredProcesses ||
                               source.ProcessesRunning != configuredProcesses ||
                               source.Processes.Count != configuredProcesses;
        var health = offline ? "offline" : badState || expectedMismatch ? "degraded" : "healthy";
        return new OperationsCollectorInstance(
            source.CollectorId,
            source.InstanceId,
            source.Status,
            health,
            source.LastSeenAt,
            age,
            source.ProcessesExpected,
            source.ProcessesRunning,
            source.ActiveJobs,
            source.QueuedJobs,
            source.SucceededJobs,
            source.RetryingJobs,
            source.FailedJobs,
            source.BlacklistedSymbols,
            source.CyclesCompleted,
            source.CurrentCycleId,
            source.HostName,
            source.Version,
            source.StartedAt,
            source.Processes.Select(static process => new OperationsCollectorProcess(
                process.WorkerId,
                process.Pid,
                process.Status,
                process.AssignedSymbols,
                process.CompletedSymbols,
                process.FailedSymbols,
                process.CurrentSymbol,
                process.LastError)).ToArray(),
            source.LastError);
    }

    private (bool IndexExists, int AssetCount) InspectWebsite()
    {
        try
        {
            var root = environment.WebRootPath;
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(environment.ContentRootPath, "wwwroot");
            var indexExists = File.Exists(Path.Combine(root, "index.html"));
            var assets = Path.Combine(root, "assets");
            var assetCount = Directory.Exists(assets)
                ? Directory.EnumerateFiles(assets, "*", SearchOption.TopDirectoryOnly).Count()
                : 0;
            return (indexExists, assetCount);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "检查网页静态文件失败。");
            return (false, 0);
        }
    }
}
