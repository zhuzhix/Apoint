using AStockMonitor.Api.Models;
using AStockMonitor.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

[ApiController]
[Route("api/internal/pair-trend-collection/next-day-validation")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class InternalPairTrendNextDayValidationController(
    CollectorGatewayRequestAuthenticator authenticator,
    PairTrendNextDayValidationService service) : ControllerBase
{
    [HttpPost("history/runs")]
    public async Task<ActionResult<NextDayValidationRunResponse>> CreateRun(
        [FromBody] NextDayValidationCreateRunRequest request,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        return await Execute(() => service.CreateHistoricalRunAsync(request, cancellationToken));
    }

    [HttpGet("history/runs/{runId:long}")]
    public async Task<ActionResult<NextDayValidationRunResponse>> GetRun(
        long runId,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        return await Execute(() => service.GetRunAsync(runId, cancellationToken));
    }

    [HttpGet("history/jobs/claim")]
    public async Task<ActionResult<NextDayValidationClaimResponse>> Claim(
        [FromQuery] long runId,
        [FromQuery] string collectorId,
        [FromQuery] int maxSymbols = PairTrendNextDayValidationService.MaximumSymbolsPerClaim,
        CancellationToken cancellationToken = default)
    {
        if (Authorize() is { } denied) return denied;
        return await Execute(() => service.ClaimAsync(runId, collectorId, maxSymbols, cancellationToken));
    }

    [HttpPost("history/jobs/batches")]
    public async Task<ActionResult<NextDayValidationAcceptedResponse>> PushBatch(
        [FromBody] NextDayValidationBatchRequest request,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        return await Execute(async () => new NextDayValidationAcceptedResponse(
            "accepted", await service.AcceptBatchAsync(request, cancellationToken), 0, 0, 0));
    }

    [HttpPost("history/jobs/complete")]
    public async Task<ActionResult<NextDayValidationAcceptedResponse>> Complete(
        [FromBody] NextDayValidationCompleteRequest request,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        return await Execute(() => service.CompleteAsync(request, cancellationToken));
    }

    [HttpPost("history/jobs/fail")]
    public async Task<ActionResult<NextDayValidationAcceptedResponse>> Fail(
        [FromBody] NextDayValidationFailLeaseRequest request,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        return await Execute(() => service.FailLeaseAsync(request, cancellationToken));
    }

    private ActionResult? Authorize()
    {
        try { authenticator.Require(Request); return null; }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }

    private static async Task<ActionResult<T>> Execute<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (ArgumentException exception) { return new BadRequestObjectResult(new { error = exception.Message }); }
        catch (KeyNotFoundException exception) { return new NotFoundObjectResult(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return new ConflictObjectResult(new { error = exception.Message }); }
    }
}
