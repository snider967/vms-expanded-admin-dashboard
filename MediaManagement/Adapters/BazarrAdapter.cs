using System.Text.Json;
using static Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters.AdapterJson;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal sealed class BazarrAdapter(IntegrationHttp http) : IReadOnlyIntegrationAdapter
{
    private const int ResponseLimit = 256 * 1024;

    public IntegrationId Id => IntegrationId.Bazarr;

    public async Task<IntegrationObservation> ObserveAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        var context = new IntegrationProbe(Id, "api/", "api", http, connection, cancellationToken);
        var identity = await context.Read("system/status", ResponseLimit);
        using var status = identity.Document;
        var version = status is null ? null : SafeVersion(Text(Property(status.RootElement, "data"), "bazarr_version"), connection.Credential);
        if (IntegrationProbe.StopsObservation(identity.Failure))
            return context.Create(version, ServiceHealth.Unknown, identity.Failure);

        var health = ServiceHealth.Unknown;
        await context.Collect("health", "system/health", ResponseLimit, root => ReadHealth(root, out health));
        await context.Collect("subtitles", "badges", ResponseLimit, ReadBadges);
        await context.Collect("providers", "providers", ResponseLimit, ReadProviders);
        await context.Collect("activity", "history/stats?timeFrame=week", ResponseLimit, ReadActivity);
        return context.Create(version, health, identity.Failure);
    }

    private static CapabilityObservation ReadHealth(JsonElement root, out ServiceHealth health)
    {
        var items = Property(root, "data");
        health = ServiceHealth.Unknown;
        if (items.ValueKind != JsonValueKind.Array) return Invalid("health");
        var count = items.GetArrayLength();
        health = count == 0 ? ServiceHealth.Healthy : ServiceHealth.Degraded;
        return Available("health", new IntegrationMetric("reportedHealthIssues", count));
    }

    private static CapabilityObservation ReadBadges(JsonElement root)
    {
        var metrics = new List<IntegrationMetric>(2);
        if (Count(root, "episodes") is { } episodes) metrics.Add(new("wantedEpisodeSubtitles", episodes));
        if (Count(root, "movies") is { } movies) metrics.Add(new("wantedMovieSubtitles", movies));
        return metrics.Count switch
        {
            2 => new("subtitles", CapabilityStatus.Available, metrics),
            1 => new("subtitles", CapabilityStatus.Degraded, metrics, "incomplete-badge-counts"),
            _ => Invalid("subtitles")
        };
    }

    private static CapabilityObservation ReadProviders(JsonElement root)
    {
        var items = Property(root, "data");
        if (items.ValueKind != JsonValueKind.Array) return Invalid("providers");
        var issues = 0;
        var unavailable = false;
        foreach (var row in items.EnumerateArray())
        {
            var status = Text(row, "status");
            if (string.IsNullOrWhiteSpace(status)) unavailable = true;
            else if (!string.Equals(status, "Good", StringComparison.OrdinalIgnoreCase)) issues++;
        }
        var total = new IntegrationMetric("reportedProviders", items.GetArrayLength());
        return unavailable ? new("providers", CapabilityStatus.Degraded, [total], "missing-provider-status")
            : Available("providers", total, new("reportedProviderIssues", issues));
    }

    private static CapabilityObservation ReadActivity(JsonElement root)
    {
        var metrics = new List<IntegrationMetric>(2);
        if (SumActivity(Property(root, "series")) is { } episodes) metrics.Add(new("episodeSubtitleHistoryEventsWeek", episodes));
        if (SumActivity(Property(root, "movies")) is { } movies) metrics.Add(new("movieSubtitleHistoryEventsWeek", movies));
        return metrics.Count switch
        {
            2 => new("activity", CapabilityStatus.Available, metrics),
            1 => new("activity", CapabilityStatus.Degraded, metrics, "incomplete-history"),
            _ => Invalid("activity")
        };
    }

    private static long? SumActivity(JsonElement rows)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 8) return null;
        long total = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (Count(row, "count") is not { } count) return null;
            total += count;
        }
        return total;
    }

}
