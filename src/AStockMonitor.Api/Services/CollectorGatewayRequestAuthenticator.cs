using System.Security.Cryptography;
using System.Text;
using AStockMonitor.Application.Collection;
using Grpc.Core;

namespace AStockMonitor.Api.Services;

public sealed class CollectorGatewayRequestAuthenticator(
    IConfiguration configuration,
    CollectorControlOptions options)
{
    public void Require(HttpRequest request)
    {
        var configuredKey = configuration["CollectorControl:GatewayApiKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
            throw new InvalidOperationException("CollectorControl:GatewayApiKey must be configured for gateway endpoints.");

        if (!request.Headers.TryGetValue("X-Collector-Gateway-Key", out var suppliedKey))
            throw new UnauthorizedAccessException("Collector Gateway authentication failed.");
        RequireKey(configuredKey, suppliedKey.ToString());

        if (!options.Enabled)
            throw new InvalidOperationException("Collector control is disabled.");
    }

    public void Require(Metadata headers)
    {
        var configuredKey = configuration["CollectorControl:GatewayApiKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "CollectorControl:GatewayApiKey must be configured for gateway endpoints."));
        var suppliedKey = headers.GetValue("x-collector-gateway-key");
        if (string.IsNullOrWhiteSpace(suppliedKey))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Collector Gateway authentication failed."));
        try { RequireKey(configuredKey, suppliedKey); }
        catch (UnauthorizedAccessException) { throw new RpcException(new Status(StatusCode.Unauthenticated, "Collector Gateway authentication failed.")); }
        if (!options.Enabled)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Collector control is disabled."));
    }

    private static void RequireKey(string expected, string actual)
    {
        if (!FixedTimeEquals(expected, actual))
            throw new UnauthorizedAccessException("Collector Gateway authentication failed.");
    }

    private static bool FixedTimeEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
}
