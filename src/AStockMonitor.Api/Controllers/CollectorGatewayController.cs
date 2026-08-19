using AStockMonitor.Api.Services;
using AStockMonitor.Application.Collection;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

[ApiController]
[Route("api/collector-gateways")]
public sealed class CollectorGatewayController(
    ICollectorCommandRepository commandRepository,
    CollectorGatewayRequestAuthenticator authenticator) : ControllerBase
{
    [HttpPost("{gatewayId}/heartbeat")]
    public async Task<IActionResult> Heartbeat(
        string gatewayId,
        [FromBody] GatewayHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        Require(gatewayId);
        await commandRepository.RecordGatewayHeartbeatAsync(
            gatewayId, request.DisplayName, request.ProtocolVersion, request.Status,
            request.Error, cancellationToken);
        return NoContent();
    }

    [HttpPost("{gatewayId}/commands/claim")]
    public async Task<ActionResult<IReadOnlyCollection<CollectorCommand>>> Claim(
        string gatewayId,
        [FromQuery] int maxCount = 4,
        CancellationToken cancellationToken = default)
    {
        Require(gatewayId);
        var commands = await commandRepository.ClaimPendingAsync(gatewayId, maxCount, cancellationToken);
        return Ok(commands);
    }

    [HttpPost("{gatewayId}/commands/{commandId:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(
        string gatewayId, Guid commandId, CancellationToken cancellationToken)
    {
        Require(gatewayId);
        await commandRepository.MarkAcknowledgedAsync(commandId, gatewayId, cancellationToken);
        return NoContent();
    }

    [HttpPost("{gatewayId}/commands/{commandId:guid}/complete")]
    public async Task<IActionResult> Complete(
        string gatewayId, Guid commandId, CancellationToken cancellationToken)
    {
        Require(gatewayId);
        await commandRepository.MarkCompletedAsync(commandId, gatewayId, cancellationToken);
        return NoContent();
    }

    [HttpPost("{gatewayId}/commands/{commandId:guid}/fail")]
    public async Task<IActionResult> Fail(
        string gatewayId, Guid commandId, [FromBody] CommandFailureRequest request,
        CancellationToken cancellationToken)
    {
        Require(gatewayId);
        await commandRepository.MarkFailedAsync(commandId, gatewayId, request.Error, cancellationToken);
        return NoContent();
    }

    private void Require(string gatewayId)
    {
        if (string.IsNullOrWhiteSpace(gatewayId)) throw new ArgumentException("Gateway id is required.");
        authenticator.Require(Request);
    }
}

public sealed record GatewayHeartbeatRequest(
    string DisplayName,
    int ProtocolVersion,
    string Status,
    string? Error = null);

public sealed record CommandFailureRequest(string Error);
