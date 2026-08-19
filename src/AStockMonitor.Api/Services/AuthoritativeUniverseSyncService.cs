using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AStockMonitor.Api.Models;
using AStockMonitor.Application.Collection;

namespace AStockMonitor.Api.Services;

public sealed class AuthoritativeUniverseSyncService(
    IAuthoritativeUniverseRepository repository,
    AuthoritativeUniverseOptions options)
{
    private static readonly TimeZoneInfo ChinaTimeZone = ResolveChinaTimeZone();
    private static readonly Regex ASharePattern = new(
        @"^(SHSE\.(600|601|603|605|688)|SZSE\.(000|001|002|003|300|301))[0-9]{3}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex StNamePrefixPattern = new(
        @"^(?:\*ST|ST|S\*ST|SST)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));
    private static readonly JsonSerializerOptions CanonicalJsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<AuthoritativeUniverseSyncResult> SynchronizeAsync(
        AuthoritativeUniverseSyncRequest request,
        CancellationToken cancellationToken)
    {
        var chinaNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ChinaTimeZone);
        var today = DateOnly.FromDateTime(chinaNow.DateTime);
        var maximumBackfillDays = Math.Clamp(options.MaximumHistoricalBackfillDays, 1, 31);
        var oldestAllowedDate = today.AddDays(-maximumBackfillDays);
        if (request.TradingDate > today || request.TradingDate < oldestAllowedDate)
            throw new ArgumentException(
                $"只接受 {oldestAllowedDate:yyyy-MM-dd} 至 {today:yyyy-MM-dd} 的权威股票池。");
        var isHistoricalBackfill = request.TradingDate < today;

        var collectorId = RequireIdentifier(request.CollectorId, "collectorId", 96);
        var source = RequireIdentifier(request.Source, "source", 32);
        var expectedSource = isHistoricalBackfill ? "dongcai-gm-history" : "dongcai-gm";
        if (!source.Equals(expectedSource, StringComparison.Ordinal))
            throw new ArgumentException(
                $"{request.TradingDate:yyyy-MM-dd} 的权威股票池来源必须是 {expectedSource}。");
        var sourceUpdatedAt = request.SourceUpdatedAt.ToUniversalTime();
        var freshness = TimeSpan.FromHours(Math.Clamp(options.SourceFreshnessHours, 1, 48));
        if (sourceUpdatedAt > DateTimeOffset.UtcNow.AddMinutes(5) ||
            sourceUpdatedAt < DateTimeOffset.UtcNow.Subtract(freshness))
            throw new ArgumentException("sourceUpdatedAt 不是当前有效的权威数据时间。");

        var inputs = request.Symbols ?? Array.Empty<AuthoritativeUniverseSymbolRequest>();
        if (!request.IsTradingDay && inputs.Count != 0)
            throw new ArgumentException("非交易日确认的 symbols 必须为空。");
        if (request.IsTradingDay)
        {
            var minimum = Math.Clamp(options.MinimumTradingDaySymbols, 1, 20_000);
            var maximum = Math.Clamp(options.MaximumTradingDaySymbols, minimum, 20_000);
            if (inputs.Count < minimum || inputs.Count > maximum)
                throw new ArgumentException(
                    $"交易日权威 A 股列表必须包含 {minimum} 到 {maximum} 只证券，当前为 {inputs.Count}。");
        }

        var symbols = new List<AuthoritativeUniverseSymbol>(inputs.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            var symbol = NormalizeSymbol(input.Symbol);
            if (!seen.Add(symbol))
                throw new ArgumentException($"权威股票池包含重复证券 {symbol}。");
            var name = string.IsNullOrWhiteSpace(input.Name)
                ? throw new ArgumentException($"{symbol} 缺少证券名称。")
                : input.Name.Trim();
            if (name.Length > 128)
                throw new ArgumentException($"{symbol} 的证券名称超过 128 个字符。");
            var nameIsSt = NameIndicatesSt(name);
            // 当日快照以当前名称前缀作为 ST 的双重校验。历史补算必须以
            // get_history_instruments 当日 sec_level 为准；当前名称可能已经摘帽或戴帽，
            // 因此不能反过来用今天的名称覆盖历史状态。
            if (!isHistoricalBackfill && input.IsSt != nameIsSt)
                throw new ArgumentException(
                    $"{symbol} 的 isSt={input.IsSt.ToString().ToLowerInvariant()} 与名称前缀判定 " +
                    $"isSt={nameIsSt.ToString().ToLowerInvariant()} 不一致，拒绝整批同步。");
            if (input.ListDate is { } listDate && listDate > request.TradingDate)
                throw new ArgumentException($"{symbol} 的上市日期晚于股票池交易日。");
            if (input.DelistDate is { } delistDate && delistDate < request.TradingDate)
                throw new ArgumentException($"{symbol} 在股票池交易日前已经退市。");

            var exchange = symbol[..4];
            var isEligible = !input.IsSt && !input.IsSuspended;
            symbols.Add(new AuthoritativeUniverseSymbol(
                symbol, name, exchange, input.IsSt, input.IsSuspended, isEligible,
                input.ListDate, input.DelistDate));
        }
        if (request.IsTradingDay)
        {
            var minimumEligible = Math.Clamp(
                options.MinimumEligibleTradingDaySymbols, 1, 20_000);
            var eligibleCount = symbols.Count(static item => item.IsEligible);
            if (eligibleCount < minimumEligible)
                throw new ArgumentException(
                    $"交易日权威股票池至少需要 {minimumEligible} 只可采集证券，" +
                    $"当前仅 {eligibleCount} 只，拒绝整批同步。");
        }
        symbols.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Symbol, right.Symbol));

        var canonicalPayload = JsonSerializer.Serialize(new
        {
            tradingDate = request.TradingDate,
            request.IsTradingDay,
            source,
            symbols
        }, CanonicalJsonOptions);
        var payloadHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload))).ToLowerInvariant();
        var universeVersion = $"official-{request.TradingDate:yyyyMMdd}-{payloadHash[..16]}";
        return await repository.SynchronizeAsync(new AuthoritativeUniverseSubmission(
            collectorId,
            request.TradingDate,
            request.IsTradingDay,
            source,
            sourceUpdatedAt.UtcDateTime,
            universeVersion,
            payloadHash,
            symbols), cancellationToken);
    }

    private static string NormalizeSymbol(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("symbol 不能为空。");
        var result = value.Trim().ToUpperInvariant();
        if (!ASharePattern.IsMatch(result))
            throw new ArgumentException($"{result} 不是严格沪深 A 股代码。");
        return result;
    }

    public static bool NameIndicatesSt(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = name.Trim().ToUpperInvariant().Replace('＊', '*');
        return StNamePrefixPattern.IsMatch(normalized);
    }

    private static string RequireIdentifier(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{field} 不能为空。");
        var result = value.Trim();
        if (result.Length > maxLength || result.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException($"{field} 格式无效。");
        return result;
    }

    private static TimeZoneInfo ResolveChinaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
    }
}
