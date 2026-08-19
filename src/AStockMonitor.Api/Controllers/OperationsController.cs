using AStockMonitor.Api.Models;
using AStockMonitor.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

[ApiController]
[Route("api/operations")]
public sealed class OperationsController(
    OperationsStatusService statusService,
    IConfiguration configuration) : ControllerBase
{
    /// <summary>返回 API、网页和 Python 采集端的综合运行状态，不返回任何凭据。</summary>
    [HttpGet("status")]
    [ProducesResponseType<OperationsStatusResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationsStatusResponse>> GetStatus(
        CancellationToken cancellationToken)
    {
        var configuredUrl = configuration["Operations:PublicWebsiteUrl"];
        var websiteUrl = string.IsNullOrWhiteSpace(configuredUrl)
            ? $"{Request.Scheme}://{Request.Host}{Request.PathBase}/"
            : configuredUrl.TrimEnd('/') + "/";
        return Ok(await statusService.GetAsync(websiteUrl, cancellationToken));
    }
}
