using AStockMonitor.Infrastructure.Persistence;
using Dapper;
using Microsoft.AspNetCore.Mvc;

namespace AStockMonitor.Api.Controllers;

/// <summary>为网页提供股票代码和名称搜索。</summary>
[ApiController]
[Route("api/instruments")]
[Produces("application/json")]
[Tags("股票基础信息")]
public sealed class InstrumentsController(IMySqlConnectionFactory connectionFactory) : ControllerBase
{
    /// <summary>按代码或名称搜索最多50只股票。</summary>
    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<InstrumentSearchDto>>> Search(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var text = query?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Ok(Array.Empty<InstrumentSearchDto>());
        }
        await using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<InstrumentSearchDto>(new CommandDefinition(
            """
            SELECT symbol Symbol,name Name,exchange Exchange,security_type SecurityType,
                   status Status
            FROM instrument
            WHERE symbol LIKE CONCAT('%',@Query,'%') OR name LIKE CONCAT('%',@Query,'%')
            ORDER BY (status='active') DESC,
                     CASE WHEN symbol=@Query OR name=@Query THEN 0 ELSE 1 END,
                     symbol LIMIT @Limit;
            """, new { Query = text, Limit = Math.Clamp(limit, 1, 50) },
            cancellationToken: cancellationToken));
        return Ok(rows.AsList());
    }

    public sealed class InstrumentSearchDto
    {
        public string Symbol { get; init; } = string.Empty;
        public string? Name { get; init; }
        public string? Exchange { get; init; }
        public string? SecurityType { get; init; }
        public string Status { get; init; } = string.Empty;
    }
}
