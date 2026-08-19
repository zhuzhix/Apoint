using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AStockMonitor.Application.Market;

namespace AStockMonitor.Application.Recovery;

public interface IMarketGapDetectionService
{
    Task<MarketGapDetectionResult> DetectAsync(
        MarketGapDetectionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 按交易日股票池和标准槽位检测K线缺口。检测本身不访问数据商，避免把无成交误判为Tick缺口；
/// Tick仅通过系统级水位产生疑似告警，真实Tick补数能力由独立开关控制。
/// </summary>
public sealed class MarketGapDetectionService(
    IMarketRecoveryRepository repository,
    MarketRecoveryOptions options) : IMarketGapDetectionService
{
    private static readonly string[] DefaultDatasets = ["5m", "30m", "60m", "1d"];

    public async Task<MarketGapDetectionResult> DetectAsync(
        MarketGapDetectionRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var datasets = NormalizeDatasets(request.Datasets);
        var eligible = await repository.GetEligibleInstrumentDaysAsync(
            request.DateFrom,
            request.DateTo,
            request.Symbols,
            cancellationToken);
        var symbolCount = eligible.Select(static item => item.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var run = await repository.BeginDetectionRunAsync(
            request,
            symbolCount,
            options.LiveOverlapSeconds,
            cancellationToken);

        try
        {
            var nowChina = ChinaMarketSession.ToChinaTime(DateTimeOffset.UtcNow).DateTime;
            // Scheduled official pulls pass the exact completed boundary. Manual and
            // legacy scans retain the configured grace-period calculation.
            var cutoff = request.CompletedBefore
                         ?? nowChina.AddSeconds(-Math.Max(0, options.CompletedBarGraceSeconds));
            var detected = new List<DetectedMarketGap>();
            long expectedSlots = 0;
            long existingSlots = 0;

            foreach (var dayGroup in eligible.GroupBy(static item => item.TradingDate))
            {
                foreach (var dataset in datasets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var expected = ChinaBarSlotGenerator.Generate(dayGroup.Key, dataset)
                        .Where(slot => slot.Eob <= cutoff)
                        .ToArray();
                    if (expected.Length == 0)
                    {
                        continue;
                    }

                    var dayItems = dayGroup.ToArray();
                    expectedSlots += (long)expected.Length * dayItems.Length;
                    var existingBySymbol = await repository.GetExistingBarEndsAsync(
                        dayItems.Select(static item => item.Symbol).ToArray(),
                        dayGroup.Key,
                        dataset,
                        cancellationToken);
                    foreach (var item in dayItems)
                    {
                        var existing = existingBySymbol.GetValueOrDefault(item.Symbol)
                            ?? new HashSet<DateTime>();
                        existingSlots += expected.Count(slot => existing.Contains(slot.Eob));
                        var missing = expected.Where(slot => !existing.Contains(slot.Eob)).ToArray();
                        detected.AddRange(GroupMissing(item, dataset, missing));
                    }
                }
            }

            var saved = await repository.SaveDetectedGapsAsync(
                run.Id,
                detected,
                createRecoveryItems: !request.DryRun && options.OfficialBarBackfillEnabled,
                cancellationToken);
            var resultJson = JsonSerializer.Serialize(new
            {
                eligibleSymbolDays = eligible.Count,
                expectedSlots,
                existingSlots,
                missingSlots = expectedSlots - existingSlots,
                gaps = saved.Count
            });
            var finalStatus = request.DryRun
                ? "detected"
                : saved.Count == 0 ? "completed" : "planned";
            run = await repository.FinishDetectionRunAsync(
                run.Id,
                finalStatus,
                saved.Count,
                resultJson,
                null,
                cancellationToken);
            return new MarketGapDetectionResult(
                run,
                saved,
                eligible.Count,
                expectedSlots,
                existingSlots);
        }
        catch (Exception exception)
        {
            await repository.FinishDetectionRunAsync(
                run.Id,
                "failed",
                0,
                null,
                exception.Message,
                CancellationToken.None);
            throw;
        }
    }

    private static IEnumerable<DetectedMarketGap> GroupMissing(
        EligibleInstrumentDay item,
        string dataset,
        IReadOnlyList<(DateTime Bob, DateTime Eob)> missing)
    {
        if (missing.Count == 0)
        {
            yield break;
        }

        var segmentStart = missing[0].Bob;
        var segmentEnd = missing[0].Eob;
        var count = 1;
        for (var index = 1; index < missing.Count; index++)
        {
            var current = missing[index];
            if (current.Bob == segmentEnd)
            {
                segmentEnd = current.Eob;
                count++;
                continue;
            }

            yield return Create(segmentStart, segmentEnd, count);
            segmentStart = current.Bob;
            segmentEnd = current.Eob;
            count = 1;
        }

        yield return Create(segmentStart, segmentEnd, count);

        DetectedMarketGap Create(DateTime start, DateTime end, int missingCount)
        {
            var identity = string.Join('|', item.Symbol, dataset, start.ToString("O"), end.ToString("O"));
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                .ToLowerInvariant();
            return new DetectedMarketGap(
                key,
                item.Symbol,
                dataset,
                dataset,
                item.TradingDate,
                start,
                end,
                missingCount,
                0,
                missingCount,
                dataset == "1d" ? "error" : "warning");
        }
    }

    private static IReadOnlyCollection<string> NormalizeDatasets(IReadOnlyCollection<string>? values)
    {
        var normalized = values is null || values.Count == 0
            ? DefaultDatasets
            : values.Select(static value => value.Trim().ToLowerInvariant()).Distinct().ToArray();
        var invalid = normalized.Except(DefaultDatasets, StringComparer.OrdinalIgnoreCase).ToArray();
        if (invalid.Length > 0)
        {
            throw new ArgumentException(
                $"UNSUPPORTED_DATASET: only 5m,30m,60m,1d are supported; invalid={string.Join(',', invalid)}");
        }

        return normalized;
    }

    private static void Validate(MarketGapDetectionRequest request)
    {
        if (request.DateFrom > request.DateTo)
        {
            throw new ArgumentException("DateFrom cannot be after DateTo");
        }

        if (request.DateTo.DayNumber - request.DateFrom.DayNumber > 31)
        {
            throw new ArgumentException("One detection request is limited to 31 calendar days");
        }
    }
}
