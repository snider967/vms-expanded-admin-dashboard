using System.Text.Json;
using static Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters.AdapterJson;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal sealed class QBittorrentAdapter(IntegrationHttp http) : IReadOnlyIntegrationAdapter
{
    private const string Prefix = "api/v2/";
    private const int SampleSize = 50;

    public IntegrationId Id => IntegrationId.QBittorrent;

    public async Task<IntegrationObservation> ObserveAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        string? session = null;
        if (connection.Authentication == Authentication.Session)
        {
            try { session = await http.LoginQBittorrent(connection, cancellationToken); }
            catch (IntegrationReadException e)
            {
                var failure = e.Failure == ReadFailure.Authorization ? ReadFailure.Authentication : e.Failure;
                return new IntegrationProbe(Id, Prefix, "v2", http, connection, cancellationToken)
                    .Create(null, ServiceHealth.Unknown, failure, retryAfter: e.RetryAfter);
            }
        }

        var probe = new IntegrationProbe(Id, Prefix, "v2", http, connection, cancellationToken, session);
        var identity = await probe.ReadText("app/version");
        var version = SafeVersion(identity.Value, connection.Credential);
        if (IntegrationProbe.StopsObservation(identity.Failure))
            return probe.Create(version, ServiceHealth.Unknown, identity.Failure);

        var api = await probe.ReadText("app/webapiVersion");
        if (!IntegrationProbe.StopsObservation(api.Failure))
        {
            await probe.Collect("transferRates", "transfer/info", 32 * 1024, ReadRates);
            await probe.Collect("transfers", $"torrents/info?limit={SampleSize}", 512 * 1024, ReadTransfers);
        }
        return probe.Create(version, ServiceHealth.Unknown, identity.Failure ?? api.Failure,
            SafeVersion(api.Value, connection.Credential));
    }

    private static CapabilityObservation ReadRates(JsonElement root)
    {
        var metrics = new List<IntegrationMetric>(2);
        if (Long(root, "dl_info_speed") is { } download) metrics.Add(new("downloadBytesPerSecond", download));
        if (Long(root, "up_info_speed") is { } upload) metrics.Add(new("uploadBytesPerSecond", upload));
        return metrics.Count switch
        {
            2 => new("transferRates", CapabilityStatus.Available, metrics),
            1 => new("transferRates", CapabilityStatus.Degraded, metrics, "incomplete-transfer-rates"),
            _ => Invalid("transferRates")
        };
    }

    private static CapabilityObservation ReadTransfers(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > SampleSize) return Invalid("transfers");
        var metrics = new List<IntegrationMetric> { new("transferSampleSize", root.GetArrayLength()) };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["sampleActiveDownloads"] = 0, ["sampleQueued"] = 0, ["sampleStalled"] = 0,
            ["sampleSeeding"] = 0, ["sampleErrors"] = 0
        };
        var downloadProgress = new List<double>();
        var knownEtas = new List<long>();
        var unknown = false;
        var active = 0;
        foreach (var row in root.EnumerateArray())
        {
            switch (Text(row, "state")?.ToLowerInvariant())
            {
                case "downloading":
                case "forceddl":
                    counts["sampleActiveDownloads"]++;
                    active++;
                    if (Property(row, "progress") is { ValueKind: JsonValueKind.Number } progress
                        && progress.TryGetDouble(out var fraction) && fraction is >= 0 and <= 1) downloadProgress.Add(fraction);
                    if (Long(row, "eta") is > 0 and < 8640000 and var eta) knownEtas.Add(eta);
                    break;
                case "queueddl":
                case "queuedup": counts["sampleQueued"]++; break;
                case "stalleddl":
                case "stalledup": counts["sampleStalled"]++; break;
                case "uploading":
                case "forcedup": counts["sampleSeeding"]++; break;
                case "error":
                case "missingfiles": counts["sampleErrors"]++; break;
                case "pauseddl": case "pausedup": case "stoppeddl": case "stoppedup":
                case "metadl": case "forcedmetadl": case "allocating": case "checkingup": case "checkingdl":
                case "checkingresumedata": case "moving": break;
                default: unknown = true; break;
            }
        }
        if (unknown) return new("transfers", CapabilityStatus.Degraded, metrics, "unknown-torrent-state");
        metrics.AddRange(counts.Select(pair => new IntegrationMetric(pair.Key, pair.Value)));
        if (active > 0 && downloadProgress.Count == active) metrics.Add(new("sampleMeanDownloadProgressPercent", downloadProgress.Average() * 100));
        if (active > 0 && knownEtas.Count == active) metrics.Add(new("sampleLongestDownloadEtaSeconds", knownEtas.Max()));
        return new("transfers", CapabilityStatus.Available, metrics);
    }

    private static long? Long(JsonElement root, string key) => Property(root, key) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt64(out var number) && number >= 0 ? number : null;
}
