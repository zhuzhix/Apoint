using AStockMonitor.Api.Models;
using AStockMonitor.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

[ApiController]
[Route("api/internal/operations")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class InternalOperationsController(
    CollectorGatewayRequestAuthenticator authenticator,
    CollectorOperationsReportService reportService) : ControllerBase
{
    [HttpPost("collector-heartbeat")]
    public async Task<ActionResult<CollectorHeartbeatAcceptedResponse>> Heartbeat(
        [FromBody] CollectorHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        if (Authorize() is { } denied) return denied;
        try
        {
            await reportService.RecordHeartbeatAsync(request, cancellationToken);
            return Ok(new CollectorHeartbeatAcceptedResponse("accepted", DateTime.UtcNow));
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
