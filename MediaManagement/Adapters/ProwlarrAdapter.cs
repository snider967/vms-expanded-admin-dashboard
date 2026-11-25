using System.Globalization;
using System.Text.Json;
using static Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters.AdapterJson;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal sealed class ProwlarrAdapter(IntegrationHttp http, TimeProvider? clock = null) : IReadOnlyIntegrationAdapter
{
    private const int SummaryLimit = 256 * 1024;
    private const int ListLimit = 1024 * 1024;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IntegrationId Id => IntegrationId.Prowlarr;

    public async Task<IntegrationObservation> ObserveAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        var context = new IntegrationProbe(Id, "api/v1/", "v1", http, connection, cancellationToken);
        var identity = await context.Read("system/status", SummaryLimit);
        using var status = identity.Document;
        var version = status is null ? null : SafeVersion(Text(status.RootElement, "version"), connection.Credential);
        if (IntegrationProbe.StopsObservation(identity.Failure))
            return context.Create(version, ServiceHealth.Unknown, identity.Failure);

        var now = _clock.GetUtcNow();
        var health = ServiceHealth.Unknown;
        await context.Collect("health", "health", SummaryLimit, root => ReadArrHealth(root, out health));
        await context.Collect("indexers", "indexer", ListLimit, ReadIndexers);
        await context.Collect("indexerStatus", "indexerstatus", SummaryLimit, root => ReadIndexerStatus(root, now));
        await context.Collect("statistics", $"indexerstats?startDate={DateParameter(now.AddDays(-1))}&endDate={DateParameter(now)}", ListLimit, ReadStats);
        await context.Collect("applications", "applications", ListLimit, ReadApplications);
        return context.Create(version, health, identity.Failure);
    }

    private static CapabilityObservation ReadIndexers(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return Invalid("indexers");
        var assigned = new IntegrationMetric("configuredIndexers", root.GetArrayLength());
        return EnabledCount(root) is { } enabled
            ? Available("indexers", assigned, new("enabledIndexers", enabled))
            : Partial("indexers", "unknown-indexer-enablement", assigned);
    }

    private static CapabilityObservation ReadIndexerStatus(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Array) return Invalid("indexerStatus");
        var failures = 0;
        var disabled = 0;
        var missingFailure = false;
        var missingDisabled = false;
        foreach (var row in root.EnumerateArray())
        {
            if (Date(Property(row, "mostRecentFailure"), out var failureAt))
            {
                if (failureAt is not null) failures++;
            }
            else missingFailure = true;

            if (Date(Property(row, "disabledTill"), out var disabledTill))
            {
                if (disabledTill > now) disabled++;
            }
            else missingDisabled = true;
        }
        var metrics = new List<IntegrationMetric>(2);
        if (!missingFailure) metrics.Add(new("indexersWithReportedFailure", failures));
        if (!missingDisabled) metrics.Add(new("indexersInBackoff", disabled));
        return metrics.Count == 2 ? new("indexerStatus", CapabilityStatus.Available, metrics)
            : new("indexerStatus", CapabilityStatus.Degraded, metrics, "incomplete-indexer-status");
    }

    private static CapabilityObservation ReadStats(JsonElement root)
    {
        var rows = Property(root, "indexers");
        if (rows.ValueKind != JsonValueKind.Array) return Invalid("statistics");
        var metrics = new List<IntegrationMetric>(3);
        var data = rows.EnumerateArray().ToArray();
        AddSum("numberOfQueries", "indexerQueries24h");
        AddSum("numberOfFailedQueries", "failedIndexerQueries24h");
        var responseTimes = data.Select(row => Count(row, "averageResponseTime")).ToArray();
        if (responseTimes.Length > 0 && responseTimes.All(time => time is > 0))
            metrics.Add(new("slowestIndexerMeanResponseMs24h", responseTimes.Max()!.Value));
        return metrics.Count == 3 || data.Length == 0
            ? new("statistics", CapabilityStatus.Available, metrics)
            : new("statistics", CapabilityStatus.Degraded, metrics, "incomplete-indexer-statistics");

        void AddSum(string field, string metric)
        {
            var values = data.Select(row => Count(row, field)).ToArray();
            if (values.All(value => value.HasValue)) metrics.Add(new(metric, values.Sum(value => (long)value!.Value)));
        }
    }

    private static CapabilityObservation ReadApplications(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return Invalid("applications");
        var metrics = new List<IntegrationMetric>(3) { new("configuredApplications", root.GetArrayLength()) };
        if (EnabledCount(root) is { } enabled) metrics.Add(new("enabledApplications", enabled));
        var syncLevel = root.EnumerateArray().Select(row => Text(row, "syncLevel")?.ToLowerInvariant()).ToArray();
        if (syncLevel.All(level => level is "disabled" or "addonly" or "fullsync"))
            metrics.Add(new("applicationsWithSyncConfigured", syncLevel.Count(level => level is not "disabled")));
        return metrics.Count == 3 ? new("applications", CapabilityStatus.Available, metrics)
            : new("applications", CapabilityStatus.Degraded, metrics, "incomplete-application-status");
    }

    private static int? EnabledCount(JsonElement root)
    {
        var count = 0;
        foreach (var row in root.EnumerateArray())
        {
            var value = Property(row, "enable");
            if (value.ValueKind == JsonValueKind.True) count++;
            else if (value.ValueKind != JsonValueKind.False) return null;
        }
        return count;
    }

    private static bool Date(JsonElement value, out DateTimeOffset? date)
    {
        date = null;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out var parsed)) return false;
        date = parsed;
        return true;
    }

    private static string DateParameter(DateTimeOffset value) =>
        Uri.EscapeDataString(value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
}
