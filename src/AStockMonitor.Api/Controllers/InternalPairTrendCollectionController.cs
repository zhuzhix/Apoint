using AStockMonitor.Api.Models;
using AStockMonitor.Api.Services;
using AStockMonitor.Application.Collection;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

/// <summary>
/// Python 四周期 K 线采集协议。该控制器只接受 API 下发计划中的证券、周期和时间窗口，
/// 不提供任何 Redis/MySQL 凭据或直连入口。
/// </summary>
[ApiController]
[Route("api/internal/pair-trend-collection")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class InternalPairTrendCollectionController(
    CollectorGatewayRequestAuthenticator authenticator,
    PairTrendCollectionPlanProvider planProvider,
    PairTrendCollectionSessionStore sessionStore,
    PairTrendCollectionComputeQueue computeQueue,
    CollectorOperationsReportService operationsReportService,
    AuthoritativeUniverseSyncService universeSyncService) : ControllerBase
{
    [HttpGet("plan")]
    public async Task<ActionResult<PairTrendCollectionPlanResponse>> GetPlan(
        [FromQuery] DateOnly? tradingDate,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        return Ok(await planProvider.GetPlanAsync(tradingDate, cancellationToken));
    }

    [HttpPost("cycles/{cycleId}/batches")]
    public ActionResult<PairTrendCollectionAcceptedResponse> PushBatch(
        string cycleId,
        [FromBody] PairTrendCollectionBatchRequest request)
    {
        if (Authorize() is { } denied) return denied;
        if (request.Bars is null || request.Bars.Count == 0)
            return BadRequest("bars 不能为空。");
        if (request.Bars.Count > 3_000)
            return BadRequest("单批 K 线不能超过 3000 条。");

        try
        {
            var accepted = sessionStore.AcceptBatch(cycleId, request.Bars);
            return Ok(new PairTrendCollectionAcceptedResponse(cycleId, $"accepted:{accepted}"));
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    [HttpPost("cycles/{cycleId}/complete")]
    public async Task<ActionResult<PairTrendCollectionAcceptedResponse>> Complete(
        string cycleId,
        [FromBody] PairTrendCollectionCompleteRequest request,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        if (request.CompletedSymbols is null)
            return BadRequest("completedSymbols 不能为空。");

        try
        {
            var work = sessionStore.Complete(cycleId, request);
            await computeQueue.EnqueueAsync(work, cancellationToken);
            return Accepted(new PairTrendCollectionAcceptedResponse(cycleId, "computing"));
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    [HttpPost("cycles/{cycleId}/abort")]
    public ActionResult Abort(string cycleId, [FromBody] PairTrendCollectionAbortRequest request)
    {
        if (Authorize() is { } denied) return denied;
        try
        {
            sessionStore.Abort(cycleId, request.Error);
            return NoContent();
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    [HttpGet("status")]
    public ActionResult<PairTrendCollectionStatusResponse> GetStatus()
    {
        if (Authorize() is { } denied) return denied;
        return Ok(sessionStore.GetStatus());
    }

    [HttpPost("blacklist")]
    public async Task<ActionResult<CollectorBlacklistResponse>> Blacklist(
        [FromBody] CollectorBlacklistRequest request,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        try
        {
            var entry = await operationsReportService.BlacklistAsync(request, cancellationToken);
            return Ok(new CollectorBlacklistResponse(
                entry.Symbol,
                DateTime.SpecifyKind(entry.BlacklistedAt, DateTimeKind.Utc),
                DateTime.SpecifyKind(entry.ExpiresAt, DateTimeKind.Utc),
                entry.FailureCount));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPost("universe")]
    public async Task<ActionResult<AuthoritativeUniverseSyncResult>> SynchronizeUniverse(
        [FromBody] AuthoritativeUniverseSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        try
        {
            return Ok(await universeSyncService.SynchronizeAsync(request, cancellationToken));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    private ActionResult? Authorize()
    {
        try
        {
            authenticator.Require(Request);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }
}
