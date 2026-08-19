namespace AStockMonitor.Api.Services;

internal static class OperationsHealthPolicy
{
    internal static string ResolveOverallStatus(
        string apiStatus,
        string websiteStatus,
        string collectorStatus,
        string collectionStatus,
        int activeBlacklistCount)
    {
        if (apiStatus != "healthy" || websiteStatus != "healthy")
            return "unhealthy";
        if (collectorStatus == "offline" || collectionStatus == "failed")
            return "unhealthy";
        if (collectorStatus != "healthy" || activeBlacklistCount > 0)
            return "degraded";
        return "healthy";
    }
}
