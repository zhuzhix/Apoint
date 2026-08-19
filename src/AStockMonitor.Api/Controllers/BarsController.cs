using AStockMonitor.Application.Market;
using AStockMonitor.Domain.Market;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

/// <summary>查询实时活动 K 线和已固化的收盘 K 线。</summary>
[ApiController]
[Route("api/market/bars")]
[Produces("application/json")]
[Tags("实时K线")]
public sealed class BarsController(IOfficialBarReader reader) : ControllerBase
{
    /// <summary>查询某只股票指定周期的最新 K 线，盘中可能返回尚未收盘的 BarUpdated 投影。</summary>
    /// <param name="symbol">股票代码，例如 SHSE.600000。</param>
    /// <param name="frequency">官方周期：5m、30m、60m、1d。</param>
    /// <param name="includeActive">是否优先返回Redis中的官方未闭合K线。</param>
    /// <param name="cancellationToken">客户端断开时取消查询。</param>
    /// <response code="200">查询成功。</response>
    /// <response code="400">股票代码为空或周期不受支持。</response>
    /// <response code="404">该股票尚无实时或历史 K 线。</response>
    [HttpGet("latest")]
    [ProducesResponseType(typeof(MarketBar), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MarketBar>> Latest(
        [FromQuery] string symbol,
        [FromQuery] string frequency = MarketBarFrequencies.Minute5,
        [FromQuery] bool includeActive = true,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(symbol, frequency);
        if (validation is not null)
        {
            return BadRequest(new { message = validation });
        }

        var result = await reader.GetLatestAsync(
            symbol.Trim().ToUpperInvariant(),
            frequency.ToLowerInvariant(),
            includeActive,
            cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>分页上限内查询一段时间的已关闭或已修订 K 线，结果按时间正序返回。</summary>
    /// <param name="symbol">股票代码，例如 SHSE.600000。</param>
    /// <param name="frequency">官方周期：5m、30m、60m、1d。</param>
    /// <param name="from">开始时间；默认最近7天。</param>
    /// <param name="to">结束时间；默认当前时间。</param>
    /// <param name="limit">最大返回数量，范围1～10000。</param>
    /// <param name="cancellationToken">客户端断开时取消查询。</param>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyCollection<MarketBar>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyCollection<MarketBar>>> Range(
        [FromQuery] string symbol,
        [FromQuery] string frequency = MarketBarFrequencies.Minute5,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(symbol, frequency);
        if (validation is not null)
        {
            return BadRequest(new { message = validation });
        }

        var rangeTo = to ?? DateTimeOffset.UtcNow;
        var rangeFrom = from ?? rangeTo.AddDays(-7);
        if (rangeFrom > rangeTo)
        {
            return BadRequest(new { message = "from不能晚于to" });
        }

        var result = await reader.GetBarsAsync(
            symbol.Trim().ToUpperInvariant(),
            frequency.ToLowerInvariant(),
            rangeFrom,
            rangeTo,
            Math.Clamp(limit, 1, 10_000),
            cancellationToken);
        return Ok(result);
    }

    private static string? Validate(string symbol, string frequency)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return "symbol不能为空";
        }

        return MarketBarFrequencies.IsSupported(frequency)
            ? null
            : "UNSUPPORTED_FREQUENCY: frequency只支持5m、30m、60m、1d";
    }
}
