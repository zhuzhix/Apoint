using AStockMonitor.Api.Models;
using AStockMonitor.Api.Services;
using AStockMonitor.Application.Analytics;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

/// <summary>查询正式对子历史数据和上海交易日当天盘中状态。</summary>
[ApiController]
[Route("api/pair-trends")]
[Produces("application/json")]
[Tags("对子顶底查询")]
public sealed class PairTrendQueryController(
    PairTrendQueryOptions options,
    PairTrendQueryService queryService) : ControllerBase
{
    [HttpGet("capabilities")]
    public ActionResult<PairTrendCapabilitiesResponse> GetCapabilities() => Ok(
        new PairTrendCapabilitiesResponse(
            options.HistoricalDataEnabled,
            options.IntradayEnabled,
            options.HistoricalReplayEnabled,
            "Asia/Shanghai",
            options.IntradayRefreshSeconds,
            options.MaximumDateRangeDays));

    [HttpGet("data/stock-groups")]
    [HttpGet(PairTrendQueryPolicy.LiveStockGroupsRoute)]
    public async Task<ActionResult<PairTrendStockGroupPage>> GetHistoricalGroups(
        [FromQuery] PairTrendGroupQuery query,
        CancellationToken cancellationToken = default) =>
        await Execute(() => queryService.GetHistoricalGroupsAsync(
            query, DateTimeOffset.UtcNow, cancellationToken));

    [HttpGet("data/events")]
    public async Task<ActionResult<PairTrendTimelinePage>> GetHistoricalEvents(
        [FromQuery] PairTrendEventQuery query,
        CancellationToken cancellationToken = default) =>
        await Execute(() => queryService.GetHistoricalEventsAsync(
            query, DateTimeOffset.UtcNow, cancellationToken));

    [HttpGet("data/stock-groups/{symbol}/events")]
    [HttpGet(PairTrendQueryPolicy.LiveStockGroupEventsRoute)]
    public async Task<ActionResult<PairTrendTimelinePage>> GetHistoricalGroupEvents(
        string symbol,
        [FromQuery] PairTrendEventQuery query,
        CancellationToken cancellationToken = default) =>
        await Execute(() => queryService.GetHistoricalEventsAsync(
            WithSymbol(query, symbol), DateTimeOffset.UtcNow, cancellationToken));

    [HttpGet("intraday/status")]
    public async Task<ActionResult<PairTrendIntradayStatusResponse>> GetIntradayStatus(
        CancellationToken cancellationToken = default) =>
        await Execute(() => queryService.GetIntradayStatusAsync(
            DateTimeOffset.UtcNow, cancellationToken));

    [HttpGet("intraday/stock-groups")]
    public async Task<ActionResult<PairTrendStockGroupPage>> GetIntradayGroups(
        [FromQuery] PairTrendGroupQuery query,
        CancellationToken cancellationToken = default) =>
        await Execute(() => queryService.GetIntradayGroupsAsync(
            ForIntraday(query), DateTimeOffset.UtcNow, cancellationToken));

    [HttpGet("intraday/stock-groups/{symbol}/events")]
    public async Task<ActionResult<PairTrendTimelinePage>> GetIntradayGroupEvents(
        string symbol,
        [FromQuery] PairTrendEventQuery query,
        CancellationToken cancellationToken = default) =>
        await Execute(() => queryService.GetIntradayEventsAsync(
            WithSymbol(ForIntraday(query), symbol), DateTimeOffset.UtcNow, cancellationToken));

    private async Task<ActionResult<T>> Execute<T>(Func<Task<T>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { code = "PAIR_TREND_QUERY_INVALID", message = exception.Message });
        }
        catch (PairTrendQueryDisabledException exception)
        {
            return Conflict(new { code = exception.Code });
        }
        catch (PairTrendDataQualityException exception)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { code = exception.Code });
        }
    }

    private static PairTrendGroupQuery ForIntraday(PairTrendGroupQuery query) => new()
    {
        Page = query.Page,
        PageSize = query.PageSize,
        Keyword = query.Keyword,
        PivotType = query.PivotType,
        Frequency = query.Frequency,
        StageAtEnd = query.StageAtEnd,
        ActiveAtEnd = query.ActiveAtEnd,
        IncludeInvalidated = true
    };

    private static PairTrendEventQuery ForIntraday(PairTrendEventQuery query) => new()
    {
        Page = query.Page,
        PageSize = query.PageSize,
        Keyword = query.Keyword,
        PivotType = query.PivotType,
        Frequency = query.Frequency,
        StageAtEnd = query.StageAtEnd,
        ActiveAtEnd = query.ActiveAtEnd,
        IncludeInvalidated = true,
        Symbol = query.Symbol
    };

    private static PairTrendEventQuery WithSymbol(PairTrendEventQuery query, string symbol) => new()
    {
        DateFrom = query.DateFrom,
        DateTo = query.DateTo,
        Page = query.Page,
        PageSize = query.PageSize,
        Keyword = query.Keyword,
        PivotType = query.PivotType,
        Frequency = query.Frequency,
        StageAtEnd = query.StageAtEnd,
        ActiveAtEnd = query.ActiveAtEnd,
        IncludeInvalidated = query.IncludeInvalidated,
        Symbol = symbol
    };
}
