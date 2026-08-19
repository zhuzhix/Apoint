using AStockMonitor.Application.Market;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

/// <summary>查询内存最新行情和采集器运行状态。</summary>
[ApiController]
[Route("api/market")]
[Produces("application/json")]
[Tags("实时行情")]
public sealed class MarketController(
    IMarketStateStore stateStore,
    IMarketDataReader marketDataReader,
    MarketRuntimeState runtimeState) : ControllerBase
{
    /// <summary>查询单只股票或当前内存中的全部最新行情。</summary>
    /// <param name="symbol">可选股票代码；传入时返回单条行情，不传时返回全部行情集合。</param>
    /// <param name="cancellationToken">客户端断开连接时取消分层查询。</param>
    /// <returns>单条最新行情或全部最新行情。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="404">指定股票尚无最新行情。</response>
    [HttpGet("latest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Latest(
        [FromQuery] string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            // 统一读取接口只尝试L0内存和L1 Redis；V2不再回退MySQL Tick。
            var quote = await marketDataReader.GetLatestAsync(symbol, cancellationToken);
            if (quote is null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    code = "REALTIME_DATA_UNAVAILABLE",
                    message = "进程内存和Redis中均无可用的最新Tick"
                });
            }
            var age = Math.Max(0, (DateTimeOffset.UtcNow - quote.EventTime).TotalMilliseconds);
            return Ok(new
            {
                quote.Symbol,
                quote.Price,
                quote.PreClose,
                quote.EventTime,
                quote.ReceiveTime,
                ageMilliseconds = (long)age,
                quote.Source,
                dataStatus = age <= 5_000 ? "realtime" : "stale",
                quote
            });
        }

        return Ok(stateStore.GetAll());
    }

    /// <summary>批量查询最多1000只股票的最新行情，服务端自动按Redis分片读取。</summary>
    /// <param name="symbols">逗号分隔的股票代码。</param>
    /// <param name="cancellationToken">客户端断开时取消查询。</param>
    [HttpGet("latest/batch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> LatestBatch(
        [FromQuery] string symbols,
        CancellationToken cancellationToken = default)
    {
        var requested = (symbols ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static symbol => symbol.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requested.Length == 0)
            return BadRequest(new { code = "SYMBOLS_REQUIRED", message = "symbols不能为空" });
        if (requested.Length > 1_000)
            return BadRequest(new { code = "TOO_MANY_SYMBOLS", message = "单次最多查询1000只股票" });

        var quotes = await marketDataReader.GetLatestBatchAsync(requested, cancellationToken);
        if (quotes.Count == 0)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "REALTIME_DATA_UNAVAILABLE",
                message = "进程内存和Redis中均无可用的最新Tick"
            });
        }

        return Ok(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            requestedCount = requested.Length,
            returnedCount = quotes.Count,
            missingSymbols = requested.Where(symbol => !quotes.ContainsKey(symbol)),
            items = quotes.Values
        });
    }

    /// <summary>查询单只股票最近一段时间的 Tick；优先返回进程内环形缓存。</summary>
    /// <param name="symbol">股票代码，例如 SHSE.600000。</param>
    /// <param name="seconds">向前查询秒数，范围1～3600。</param>
    /// <param name="limit">最大返回数量，范围1～10000。</param>
    /// <param name="cancellationToken">客户端断开连接时取消查询。</param>
    [HttpGet("ticks/recent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RecentTicks(
        [FromQuery] string symbol,
        [FromQuery] int seconds = 300,
        [FromQuery] int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return BadRequest(new { message = "symbol不能为空" });
        }

        var boundedSeconds = Math.Clamp(seconds, 1, 3600);
        var boundedLimit = Math.Clamp(limit, 1, 10_000);
        var ticks = await marketDataReader.GetRecentTicksAsync(
            symbol,
            DateTimeOffset.UtcNow.AddSeconds(-boundedSeconds),
            boundedLimit,
            cancellationToken);
        return Ok(new
        {
            storage = "process-memory",
            capacityPerSymbol = 256,
            complete = false,
            items = ticks
        });
    }

    /// <summary>查询 API 接收计数和 Python 采集 Worker 运行快照。</summary>
    /// <returns>当前进程内的实时采集运行状态。</returns>
    /// <response code="200">查询成功。</response>
    [HttpGet("runtime")]
    [ProducesResponseType(typeof(MarketRuntimeResponse), StatusCodes.Status200OK)]
    public ActionResult<MarketRuntimeResponse> Runtime()
    {
        var now = DateTimeOffset.UtcNow;
        var workers = runtimeState.GetCollectors();

        return Ok(new MarketRuntimeResponse
        {
            GeneratedAt = now,
            AcceptedCount = runtimeState.AcceptedCount,
            DuplicateCount = runtimeState.DuplicateCount,
            RejectedCount = runtimeState.RejectedCount,
            LastReceiveTime = runtimeState.LastReceiveTime,
            LastIngestTime = runtimeState.LastIngestTime,
            ConnectedWorkers = workers.Count(static worker => worker.Connected),
            DisconnectedWorkers = workers.Count(static worker => !worker.Connected),
            StaleWorkers = workers.Count(worker =>
                worker.Connected && now - worker.LastSeenAt > TimeSpan.FromSeconds(15)),
            Workers = workers
        });
    }

    /// <summary>实时行情采集链路运行快照。</summary>
    public sealed class MarketRuntimeResponse
    {
        /// <summary>快照生成时间，UTC。</summary>
        public DateTimeOffset GeneratedAt { get; init; }

        /// <summary>API 接受的新行情事件累计数。</summary>
        public long AcceptedCount { get; init; }

        /// <summary>API 识别出的重复事件累计数。</summary>
        public long DuplicateCount { get; init; }

        /// <summary>API 拒绝的无效事件累计数。</summary>
        public long RejectedCount { get; init; }

        /// <summary>最近一次收到行情的源接收时间。</summary>
        public DateTimeOffset? LastReceiveTime { get; init; }

        /// <summary>最近一次完成内存更新的时间。</summary>
        public DateTimeOffset? LastIngestTime { get; init; }

        /// <summary>当前连接的采集 Worker 数量。</summary>
        public int ConnectedWorkers { get; init; }

        /// <summary>已经断开的采集 Worker 数量。</summary>
        public int DisconnectedWorkers { get; init; }

        /// <summary>连接正常但超过 15 秒未更新的 Worker 数量。</summary>
        public int StaleWorkers { get; init; }

        /// <summary>各采集 Worker 的心跳、队列、CPU 和内存快照。</summary>
        public IReadOnlyCollection<CollectorRuntimeSnapshot> Workers { get; init; } = [];
    }
}
