using System.Text.Json;
using static Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters.AdapterJson;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal sealed class SeerrAdapter(IntegrationHttp http) : IReadOnlyIntegrationAdapter
{
    private const int ResponseLimit = 64 * 1024;
    private static readonly (string Source, string Metric)[] Counts =
    [
        ("total", "totalRequests"), ("pending", "pendingRequests"), ("approved", "approvedRequests"),
        ("available", "availableRequests"), ("processing", "processingRequests"), ("completed", "completedRequests"),
        ("declined", "declinedRequests"), ("movie", "movieRequests"), ("tv", "tvRequests")
    ];

    public IntegrationId Id => IntegrationId.Seerr;

    public async Task<IntegrationObservation> ObserveAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        var probe = new IntegrationProbe(Id, "api/v1/", "v1", http, connection, cancellationToken);
        var identity = await probe.Read("status?versionCheck=false", ResponseLimit, authenticate: false);
        using var status = identity.Document;
        var version = status is null ? null : SafeVersion(Text(status.RootElement, "version"), connection.Credential);
        if (IntegrationProbe.StopsObservation(identity.Failure))
            return probe.Create(version, ServiceHealth.Unknown, identity.Failure);

        probe.Add(version is null ? Invalid("health") : Available("health"));
        await probe.Collect("requests", "request/count", ResponseLimit, ReadCounts);
        await probe.Collect("requestFailures", "request?take=1&skip=0&filter=failed", ResponseLimit, ReadFailures);
        return probe.Create(version, ServiceHealth.Unknown, identity.Failure);
    }

    private static CapabilityObservation ReadCounts(JsonElement root)
    {
        if (Count(root, "total") is null) return Invalid("requests");
        var metrics = new List<IntegrationMetric>(Counts.Length);
        foreach (var (source, name) in Counts)
            if (Count(root, source) is { } value) metrics.Add(new(name, value));
        return metrics.Any(metric => metric.Name == "pendingRequests") && metrics.Any(metric => metric.Name == "approvedRequests")
            ? new("requests", CapabilityStatus.Available, metrics)
            : new("requests", CapabilityStatus.Degraded, metrics, "incomplete-request-counts");
    }

    private static CapabilityObservation ReadFailures(JsonElement root)
    {
        var results = Property(root, "results");
        var total = Count(Property(root, "pageInfo"), "results");
        if (total is null || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != Math.Min(1, total.Value))
            return Invalid("requestFailures");
        // Verify the server honored its documented failed filter before relying on the reported count.
        if (total > 0 && Count(results[0], "status") != 4) return Invalid("requestFailures");
        return Available("requestFailures", new IntegrationMetric("failedRequests", total.Value));
    }
}
