using AStockMonitor.Api.Models;
using AStockMonitor.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

[ApiController]
[Route("api/internal/pair-trend-collection/wave-bottom")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class InternalWaveBottomCollectionController(
    CollectorGatewayRequestAuthenticator authenticator,
    WaveBottomCollectionService service) : ControllerBase
{
    [HttpGet("jobs/claim")]
    public async Task<ActionResult<WaveBottomClaimResponse>> Claim(
        [FromQuery] string collectorId,
        [FromQuery] int maxSymbols = WaveBottomCollectionService.MaximumSymbolsPerClaim,
        CancellationToken cancellationToken = default)
    {
        if (Authorize() is { } denied) return denied;
        try
        {
            return Ok(await service.ClaimAsync(collectorId, maxSymbols, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPost("jobs/batches")]
    public async Task<ActionResult<WaveBottomAcceptedResponse>> PushBatch(
        [FromBody] WaveBottomBatchRequest request,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        try
        {
            var accepted = await service.AcceptBatchAsync(request, cancellationToken);
            return Ok(new WaveBottomAcceptedResponse("accepted", accepted, 0, 0, 0));
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    [HttpPost("jobs/complete")]
    public async Task<ActionResult<WaveBottomAcceptedResponse>> Complete(
        [FromBody] WaveBottomCompleteRequest request,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        try
        {
            return Ok(await service.CompleteAsync(request, cancellationToken));
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    [HttpPost("jobs/fail")]
    public async Task<ActionResult<WaveBottomAcceptedResponse>> Fail(
        [FromBody] WaveBottomLeaseFailureRequest request,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        try
        {
            return Ok(await service.FailLeaseAsync(request, cancellationToken));
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return Conflict(new { error = exception.Message });
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
