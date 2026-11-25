using System.Text.Json;
using static Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters.AdapterJson;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal sealed class ArrV3Read(IntegrationHttp http)
{
    private const string Api = "api/v3/";
    private const int SummaryLimit = 256 * 1024;
    private const int CollectionLimit = 2 * 1024 * 1024;
    private const int QueuePageSize = 25;

    internal sealed record Service(IntegrationId Id, string CollectionPath, string CollectionCapability,
        string CollectionMetric, string MissingMetric, string MissingCapability, string UnknownQueueParameter);

    internal async Task<IntegrationObservation> ObserveAsync(Service service, IntegrationConnection connection, CancellationToken cancellationToken)
    {
        var context = new IntegrationProbe(service.Id, Api, "v3", http, connection, cancellationToken);
        var identity = await context.Read("system/status", SummaryLimit);
        using var status = identity.Document;
        var appVersion = status is null ? null : SafeVersion(Text(status.RootElement, "version"), connection.Credential);
        if (IntegrationProbe.StopsObservation(identity.Failure))
            return context.Create(appVersion, ServiceHealth.Unknown, identity.Failure);

        var serviceHealth = ServiceHealth.Unknown;
        await context.Collect("health", "health", SummaryLimit, root => ReadArrHealth(root, out serviceHealth));
        await context.Collect(service.CollectionCapability, service.CollectionPath, CollectionLimit,
            root => ReadMonitored(root, service.CollectionCapability, service.CollectionMetric));
        await context.Collect(service.MissingCapability, "wanted/missing?page=1&pageSize=1&monitored=true",
            SummaryLimit, root => ReadMissing(root, service.MissingCapability, service.MissingMetric));
        await context.Collect("queue", $"queue?page=1&pageSize={QueuePageSize}&{service.UnknownQueueParameter}=true",
            SummaryLimit, ReadQueue);
        return context.Create(appVersion, serviceHealth, identity.Failure);
    }

    private static CapabilityObservation ReadMonitored(JsonElement root, string capability, string metric)
    {
        if (root.ValueKind != JsonValueKind.Array) return Invalid(capability);
        var count = 0;
        foreach (var row in root.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("monitored", out var monitored)
                || monitored.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Invalid(capability);
            if (monitored.GetBoolean()) count++;
        }
        return Available(capability, new IntegrationMetric(metric, count));
    }

    private static CapabilityObservation ReadMissing(JsonElement root, string capability, string metric) =>
        Count(root, "totalRecords") is { } count ? Available(capability, new IntegrationMetric(metric, count)) : Invalid(capability);

    private static CapabilityObservation ReadQueue(JsonElement root)
    {
        if (Count(root, "totalRecords") is not { } total)
            return Invalid("queue");
        var overall = new IntegrationMetric("queueTotal", total);
        if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array
            || records.GetArrayLength() != Math.Min(total, QueuePageSize))
            return Partial("queue", "unexpected-queue-page", overall);

        var warnings = 0;
        var errors = 0;
        var imports = 0;
        var incompleteStatuses = false;
        var incompleteImports = false;
        foreach (var row in records.EnumerateArray())
        {
            var status = Text(row, "trackedDownloadStatus")?.ToLowerInvariant();
            if (status is not ("ok" or "warning" or "error"))
            {
                incompleteStatuses = true;
                continue;
            }
            if (status == "ok") continue;
            if (status == "warning") warnings++; else errors++;
            var state = Text(row, "trackedDownloadState")?.ToLowerInvariant();
            if (state is "importpending" or "importblocked") imports++;
            else if (state is not ("downloading" or "importing" or "imported" or "failedpending" or "failed" or "ignored"))
                incompleteImports = true;
        }
        var sample = new IntegrationMetric("queueSampleSize", records.GetArrayLength());
        if (incompleteStatuses) return Partial("queue", "unknown-queue-status", overall, sample);
        var warningCount = new IntegrationMetric("queueSampleWarnings", warnings);
        var errorCount = new IntegrationMetric("queueSampleErrors", errors);
        if (incompleteImports) return Partial("queue", "unknown-import-state", overall, sample, warningCount, errorCount);
        return Available("queue", overall, sample, warningCount, errorCount, new("queueSampleImportWarnings", imports));
    }

}
