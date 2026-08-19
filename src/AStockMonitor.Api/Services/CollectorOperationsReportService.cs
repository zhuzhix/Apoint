using System.Text.RegularExpressions;
using AStockMonitor.Api.Models;
using AStockMonitor.Application.Collection;

namespace AStockMonitor.Api.Services;

public sealed class CollectorOperationsReportService(
    ICollectorOperationsRepository repository,
    CollectorOperationsOptions options)
{
    private static readonly Regex SecretPattern = new(
        @"(?i)(password|passwd|pwd|token|authorization|api[_-]?key)\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public async Task RecordHeartbeatAsync(
        CollectorHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        var collectorId = RequireIdentifier(request.CollectorId, "collectorId");
        var instanceId = RequireIdentifier(request.InstanceId, "instanceId");
        var status = RequireState(request.Status, "status");
        var processLimit = RequireRange(request.ProcessLimit, 1, 64, "processLimit");
        var activeProcesses = RequireRange(request.ActiveProcesses, 0, processLimit, "activeProcesses");
        var processesExpected = RequireRange(
            request.ProcessesExpected ?? processLimit, 1, 64, "processesExpected");
        var processesRunning = RequireRange(
            request.ProcessesRunning ?? activeProcesses, 0, processLimit, "processesRunning");
        var workers = (request.Workers ?? Array.Empty<CollectorWorkerHeartbeatRequest>())
            .Take(64)
            .Select(worker => new CollectorWorkerHeartbeat(
                NormalizeWorkerId(worker.WorkerId),
                worker.Pid is > 0 ? worker.Pid : null,
                RequireState(worker.State, "worker.state"),
                RequireRange(worker.AssignedSymbols, 0, 100_000, "assignedSymbols"),
                RequireRange(worker.CompletedSymbols, 0, 100_000, "completedSymbols"),
                RequireRange(worker.FailedSymbols, 0, 100_000, "failedSymbols"),
                NormalizeOptional(worker.CurrentSymbol, 32),
                SanitizeError(worker.LastError)))
            .ToArray();

        var heartbeat = new CollectorHeartbeat(
            collectorId,
            instanceId,
            status,
            processesExpected,
            processesRunning,
            RequireRange(request.ActiveJobs, 0, 100_000, "activeJobs"),
            RequireRange(request.QueuedJobs, 0, 1_000_000, "queuedJobs"),
            RequireNonNegative(request.SucceededSymbols, "succeededSymbols"),
            RequireNonNegative(request.RetryingJobs, "retryingJobs"),
            RequireNonNegative(request.FailedSymbols, "failedSymbols"),
            RequireRange(request.BlacklistedSymbols, 0, 1_000_000, "blacklistedSymbols"),
            RequireNonNegative(request.CyclesCompleted, "cyclesCompleted"),
            NormalizeOptional(request.CurrentCycleId, 64),
            NormalizeOptional(request.HostName, 128),
            NormalizeOptional(request.Version, 64),
            request.StartedAt?.ToUniversalTime(),
            SanitizeError(request.LastError),
            workers);
        await repository.RecordHeartbeatAsync(heartbeat, cancellationToken);
    }

    public async Task<CollectorBlacklistEntry> BlacklistAsync(
        CollectorBlacklistRequest request,
        CancellationToken cancellationToken)
    {
        var collectorId = RequireIdentifier(request.CollectorId, "collectorId");
        var symbol = NormalizeSymbol(request.Symbol);
        var reason = SanitizeError(request.Reason);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason 不能为空。");
        var failureCount = RequireRange(request.FailureCount, 1, 1_000_000, "failureCount");
        var hours = Math.Clamp(options.BlacklistHours, 1, 168);
        return await repository.BlacklistAsync(
            collectorId, symbol, failureCount, reason, TimeSpan.FromHours(hours), cancellationToken);
    }

    private static string RequireIdentifier(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{field} 不能为空。");
        var result = value.Trim();
        if (result.Length > 96 || result.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException($"{field} 格式无效。");
        return result;
    }

    private static string NormalizeWorkerId(System.Text.Json.JsonElement value)
    {
        var text = value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => value.GetString(),
            System.Text.Json.JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return RequireIdentifier(text, "workerId");
    }

    private static string RequireState(string? value, string field)
    {
        var result = RequireIdentifier(value, field).ToLowerInvariant();
        if (result.Length > 24) throw new ArgumentException($"{field} 不能超过 24 个字符。");
        return result;
    }

    private static int RequireRange(int value, int minimum, int maximum, string field)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(field, $"{field} 必须在 {minimum} 到 {maximum} 之间。");
        return value;
    }

    private static long RequireNonNegative(long value, string field)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(field, $"{field} 不能为负数。");
        return value;
    }

    private static string NormalizeSymbol(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("symbol 不能为空。");
        var result = value.Trim().ToUpperInvariant();
        if (result.Length > 32 ||
            !(result.StartsWith("SHSE.", StringComparison.Ordinal) ||
              result.StartsWith("SZSE.", StringComparison.Ordinal)))
            throw new ArgumentException("symbol 必须是 SHSE./SZSE. 格式。");
        return result;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = value.Trim();
        return result[..Math.Min(result.Length, maxLength)];
    }

    internal static string? SanitizeError(string? value)
    {
        var normalized = NormalizeOptional(value, 1024);
        return normalized is null ? null : SecretPattern.Replace(normalized, "$1=***");
    }
}
