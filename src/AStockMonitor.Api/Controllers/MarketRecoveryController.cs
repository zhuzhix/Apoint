using AStockMonitor.Application.Recovery;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace AStockMonitor.Api.Controllers;

/// <summary>检测K线缺口、查询恢复状态，并管理失败补数任务。</summary>
[ApiController]
[Route("api/market-data")]
[Produces("application/json")]
[Tags("行情缺口与补数")]
public sealed class MarketRecoveryController(
    IMarketGapDetectionService detectionService,
    IMarketRecoveryRepository repository,
    IConfiguration configuration) : ControllerBase
{
    /// <summary>按交易日股票池和标准交易槽位检测缺失K线。</summary>
    /// <remarks>
    /// dryRun=true仅形成检测报告；false会为确认缺失的5m/30m/60m/1d官方K线建立补数项目。
    /// 单次请求最多31个自然日。实时订阅不受检测和补数任务阻塞。
    /// </remarks>
    [HttpPost("gaps/detect")]
    [ProducesResponseType(typeof(MarketGapDetectionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MarketGapDetectionResult>> Detect(
        [FromBody] DetectMarketGapRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await detectionService.DetectAsync(
                new MarketGapDetectionRequest(
                    request.DateFrom,
                    request.DateTo,
                    request.Symbols,
                    request.Datasets,
                    request.DetectTypes,
                    request.DryRun,
                    "manual"),
                cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    /// <summary>分页查询已检测到的行情缺口。</summary>
    [HttpGet("gaps")]
    [ProducesResponseType(typeof(PagedResult<MarketGapRecord>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<MarketGapRecord>>> Gaps(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? symbol = null,
        [FromQuery] string? dataset = null,
        [FromQuery] DateOnly? dateFrom = null,
        [FromQuery] DateOnly? dateTo = null,
        CancellationToken cancellationToken = default) =>
        Ok(await repository.QueryGapsAsync(
            page, pageSize, status, symbol, dataset, dateFrom, dateTo, cancellationToken));

    /// <summary>分页查询缺口检测和自动补数运行。</summary>
    [HttpGet("recovery-runs")]
    [ProducesResponseType(typeof(PagedResult<MarketRecoveryRunRecord>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<MarketRecoveryRunRecord>>> Runs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default) =>
        Ok(await repository.QueryRunsAsync(page, pageSize, status, cancellationToken));

    /// <summary>查询单次缺口检测或恢复运行摘要。</summary>
    [HttpGet("recovery-runs/{id:long}")]
    [ProducesResponseType(typeof(MarketRecoveryRunRecord), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MarketRecoveryRunRecord>> Run(
        long id,
        CancellationToken cancellationToken)
    {
        var result = await repository.GetRunAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>取消尚未完成的补数运行，并把未完成项目和对应误报缺口标记为已取消。</summary>
    /// <remarks>该操作保留已经写入的 K 线数据，仅终止后续领取；操作人、原因和结果会写入审计表。</remarks>
    [HttpPost("recovery-runs/{id:long}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(
        long id,
        [FromBody] CancelRecoveryRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsOperationsAuthorized()) return Unauthorized();
        var existing = await repository.GetRunAsync(id, cancellationToken);
        if (existing is null) return NotFound();
        var requestedBy = Request.Headers["X-Operator"].FirstOrDefault()
                          ?? HttpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "local-operator";
        return await repository.CancelRunAsync(id, request.Reason, requestedBy, cancellationToken)
            ? NoContent()
            : Conflict(new { message = "运行已经结束或当前状态不可取消。" });
    }

    private bool IsOperationsAuthorized()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is not null && IPAddress.IsLoopback(remote)) return true;
        var expected = configuration["HistoryOperations:ApiKey"];
        return !string.IsNullOrWhiteSpace(expected) &&
               Request.Headers.TryGetValue("X-AStock-Operations-Key", out var supplied) &&
               string.Equals(expected, supplied.ToString(), StringComparison.Ordinal);
    }

    /// <summary>把失败或等待重试的补数项目重新置为planned。</summary>
    [HttpPost("recovery-runs/{id:long}/retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retry(long id, CancellationToken cancellationToken) =>
        await repository.RetryRunAsync(id, cancellationToken)
            ? NoContent()
            : Conflict(new { message = "运行不存在、状态不可重试或没有失败项目" });

    /// <summary>行情缺口检测请求。</summary>
    public sealed class DetectMarketGapRequest
    {
        /// <summary>开始交易日期。</summary>
        public DateOnly DateFrom { get; init; }
        /// <summary>结束交易日期。</summary>
        public DateOnly DateTo { get; init; }
        /// <summary>可选股票代码；空集合表示当日全部沪深非ST且非停牌股票。</summary>
        public IReadOnlyCollection<string>? Symbols { get; init; }
        /// <summary>可选数据集：5m、30m、60m、1d；Tick和1m会返回UNSUPPORTED_DATASET。</summary>
        public IReadOnlyCollection<string>? Datasets { get; init; }
        /// <summary>检测类型：missing_slot、source_mismatch、stale_unconfirmed。</summary>
        public IReadOnlyCollection<string>? DetectTypes { get; init; }
        /// <summary>true只检测；false同时创建自动补数项目。</summary>
        public bool DryRun { get; init; } = true;
    }

    /// <summary>取消补数运行请求。</summary>
    public sealed class CancelRecoveryRunRequest
    {
        /// <summary>取消原因；用于运维审计和后续问题复盘。</summary>
        public string Reason { get; init; } = "operator cancelled";
    }
}
