using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RemoteAdminDesk;

public sealed partial class ScheduledDiagnostics(
    ILibraryManager library, IApplicationPaths paths, ILogger<ScheduledDiagnostics> logger) : IScheduledTask
{
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(3);
    private readonly DeskProbes _probes = new(library, paths);

    public string Name => Plugin.DisplayName + " diagnostic scan";
    public string Key => "RemoteAdminDeskScan";
    public string Description => "Sample media free space and recent log failures without scanning or modifying media.";
    public string Category => Plugin.DisplayName;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!ScanGate.Wait(0)) return;
        try
        {
            var (state, settings) = await DeskProbes.Run(DeskProbes.StateGate, ProbeBudget,
                ct => (DeskState.ReadDiagnostics(paths, ct), DeskSettings.Read(paths.DataPath, ct)), cancellationToken);
            var webhook = DeskSettings.Effective(DeskDiscord.DiagnosticsVariable, settings.DiagnosticsWebhook);
            var storage = await DeskProbes.Run(DeskProbes.StorageGate, ProbeBudget, _probes.ReadStorage, cancellationToken);
            progress.Report(45);
            var signals = await DeskProbes.Run(DeskProbes.LogsGate, ProbeBudget,
                ct => ReadFailures(state.LastScanAt, ct), cancellationToken);
            progress.Report(80);

            var samples = AddDailySample(state.Samples, storage);
            var readings = storage.Volumes.Where(v => v.Total.HasValue && v.Available.HasValue).ToArray();
            var low = readings.Where(IsLow).ToArray();
            // A missing volume is its own signal and must not act as a low-space recovery.
            var lowState = low.Length > 0 ? (bool?)true
                : readings.Length > 0 && readings.Length == storage.Volumes.Length
                    && !storage.Limited && storage.Locations.All(location => location.Mount is not null)
                    ? false : state.StorageWarning;
            var configuredBranches = DeskSettings.Effective("RAD_MEDIA_BRANCHES", settings.MediaBranches);
            var branches = !string.IsNullOrWhiteSpace(configuredBranches)
                ? await DeskProbes.Run(DeskProbes.StorageGate, ProbeBudget,
                    ct => _probes.ReadBranches(configuredBranches, ct), cancellationToken) : null;
            var mountState = storage.Locations.Any(location => location.Problem is not null)
                || storage.Volumes.Any(volume => volume.Problem is not null)
                || branches?.Branches.Any(volume => volume.Problem is not null) == true ? true
                : !storage.Limited && storage.Problem is null && storage.Locations.Length > 0
                    && (branches is null || (!branches.Limited && branches.Problem is null)) ? false : state.MountWarning;
            var notices = new List<string>();
            if (mountState == true && state.MountWarning != true)
                notices.Add("A media library mount is missing or its capacity could not be verified.");
            if (mountState == false && state.MountWarning == true)
                notices.Add("Media library mounts are reporting capacity again.");
            if (lowState == true && state.StorageWarning != true)
                notices.Add("Media storage crossed the planning reserve on " + low.Length + " volume(s).");
            if (lowState == false && state.StorageWarning == true)
                notices.Add("Media storage is back above the planning reserve.");
            var unseen = signals.Where(signal => !state.Delivered.Contains(signal.Key, StringComparer.Ordinal)).ToArray();
            foreach (var signal in unseen)
                notices.Add(signal.Label);

            var delivery = state.Discord;
            if (notices.Count != 0 && !string.IsNullOrWhiteSpace(webhook))
            {
                var result = await DeskDiscord.Send(webhook,
                    "VMS diagnostic scan\n" + string.Join("\n", notices.Take(10)), cancellationToken);
                delivery = DeskState.RecordDelivery(delivery, result);
                if (result != DeskDiscord.Delivered)
                {
                    // Keep notification state eligible for the next scan.
                    await Save(state with { Samples = samples, Discord = delivery }, cancellationToken);
                    logger.LogWarning("VMS Expanded Admin Dashboard could not deliver its Discord diagnostic notification.");
                    throw new InvalidOperationException("Discord diagnostic delivery failed.");
                }
            }

            var delivered = state.Delivered.Concat(unseen.Select(signal => signal.Key)).Distinct().TakeLast(64).ToArray();
            await Save(new DeskState.Diagnostics(samples, delivered, lowState, DateTimeOffset.UtcNow, delivery, mountState),
                cancellationToken);
            progress.Report(100);
        }
        finally
        {
            ScanGate.Release();
        }
    }

    private Task<bool> Save(DeskState.Diagnostics state, CancellationToken ct) =>
        DeskProbes.Run(DeskProbes.StateGate, ProbeBudget, token => DeskState.SaveDiagnostics(paths, state, token), ct);

    private static bool IsLow(DeskProbes.Volume volume)
    {
        var total = (double)volume.Total!.Value;
        var reserve = Math.Min(.15 * total, Math.Max(30d * 1024 * 1024 * 1024, .10 * total));
        return volume.Available!.Value <= reserve;
    }

    private static DeskState.Sample[] AddDailySample(DeskState.Sample[] samples, DeskProbes.StorageReport report)
    {
        var usable = report.Volumes.Where(v => v.Total.HasValue && v.Available.HasValue).ToArray();
        if (usable.Length == 0) return samples;
        return samples.Where(sample => sample.SampledAt.UtcDateTime.Date != report.SampledAt.UtcDateTime.Date)
            .Append(new DeskState.Sample(report.SampledAt, usable)).TakeLast(120).ToArray();
    }

    private Failure[] ReadFailures(DateTimeOffset? previousScan, CancellationToken ct)
    {
        if (previousScan is null) return [];
        var catalog = _probes.ReadCatalog(ct);
        var recent = catalog.Files.Where(file => file.ModifiedAt >= previousScan.Value.AddMinutes(-2)).ToArray();
        var server = recent.Where(file => !file.Name.StartsWith("FFmpeg", StringComparison.OrdinalIgnoreCase)).Take(1);
        var ffmpeg = recent.Where(file => file.Name.StartsWith("FFmpeg", StringComparison.OrdinalIgnoreCase)).Take(3);
        var failures = new List<Failure>();
        foreach (var file in server.Concat(ffmpeg))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var excerpt = _probes.ReadTail(file.Name, ct, 16 * 1024);
                var match = SignificantFailure().Matches(excerpt.Content).LastOrDefault();
                if (match is null) continue;
                var ff = file.Name.StartsWith("FFmpeg", StringComparison.OrdinalIgnoreCase);
                // Fingerprint the newest failing line without retaining its contents.
                var start = excerpt.Content.LastIndexOf('\n', match.Index) + 1;
                var end = excerpt.Content.IndexOf('\n', match.Index);
                if (end < 0) end = excerpt.Content.Length;
                var line = excerpt.Content[start..end];
                var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(file.Name + "\n" + line)));
                failures.Add(new Failure(fingerprint, ff
                    ? "A recent FFmpeg log contains a transcode or hardware acceleration failure."
                    : "A recent Jellyfin server log contains a serious storage, memory, database, or transcode error."));
            }
            catch (FileNotFoundException)
            {
                // Ignore a log that rotated while the scan was in progress.
            }
        }
        return failures.ToArray();
    }

    private sealed record Failure(string Key, string Label);

    [GeneratedRegex(@"no space left on device|disk quota exceeded|database disk image is malformed|outofmemoryexception|input/output error|ffmpeg exited with code [1-9]|error while opening encoder|error initializing output stream|conversion failed|device creation failed|no va display|failed to initialize (?:vaapi|qsv|cuda)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SignificantFailure();
}

public sealed class ScheduledMediaAlerts(
    ILibraryManager library, IApplicationPaths paths, ILogger<ScheduledMediaAlerts> logger) : IScheduledTask
{
    private const int QueryLimit = 120;
    private static readonly SemaphoreSlim JobGate = new(1, 1);
    private static readonly SemaphoreSlim QueryGate = new(1, 1);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);
    public string Name => Plugin.DisplayName + " new movies and TV";
    public string Key => "RemoteAdminDeskMedia";
    public string Description => "Send Discord alerts for newly indexed movies and television episodes.";
    public string Category => Plugin.DisplayName;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!JobGate.Wait(0)) return;
        try
        {
            var (previous, settings) = await DeskProbes.Run(DeskProbes.StateGate, Budget,
                ct => (DeskState.ReadMedia(paths, ct), DeskSettings.Read(paths.DataPath, ct)), cancellationToken);
            var webhook = DeskSettings.Effective(DeskDiscord.MediaVariable, settings.MediaWebhook);
            if (string.IsNullOrWhiteSpace(webhook))
            {
                logger.LogInformation("VMS Expanded Admin Dashboard media alerts have no Discord webhook configured.");
                return;
            }
            var current = await DeskProbes.Run(QueryGate, Budget, ReadLatest, cancellationToken);
            progress.Report(60);
            var known = previous?.Known.ToHashSet();
            var cutoff = previous?.OldestDate ?? DateTimeOffset.MinValue;
            MediaItem[] added = known is null ? [] : current.Where(item => !known.Contains(item.Id)
                && item.Date >= cutoff).Reverse().ToArray();
            if (current.Length == QueryLimit && previous is not null && current.All(item => !known!.Contains(item.Id)))
                logger.LogWarning("VMS Expanded Admin Dashboard media coverage was capped. The two latest-item samples did not overlap.");

            var currentIds = current.Select(item => item.Id).ToHashSet();
            var ids = (previous?.Known ?? []).Where(id => !currentIds.Contains(id))
                .Concat(current.Reverse().Select(item => item.Id)).TakeLast(512).ToArray();
            var oldest = current.Length == QueryLimit ? current[^1].Date : (DateTimeOffset?)null;
            var pending = (previous?.Pending ?? []).Concat(added.Select(item => item.Title)).ToArray();
            var next = pending.Length > 240
                ? previous! // Leave the query baseline untouched while the existing queue drains.
                : new DeskState.Media(ids, oldest, pending, DateTimeOffset.UtcNow, previous?.Discord);
            await Save(next, cancellationToken);

            var batch = next.Pending.Take(14).ToArray();
            if (batch.Length > 0)
            {
                var content = "VMS · New in Jellyfin · " + batch.Length + " movie(s) and episode(s)\n"
                    + string.Join("\n", batch.Select(title => "• " + title));
                var result = await DeskDiscord.Send(webhook, content, cancellationToken);
                var delivery = DeskState.RecordDelivery(next.Discord, result);
                if (result != DeskDiscord.Delivered)
                {
                    await Save(next with { Discord = delivery }, cancellationToken);
                    logger.LogWarning("VMS Expanded Admin Dashboard could not deliver its Discord media notification.");
                    throw new InvalidOperationException("Discord media delivery failed.");
                }
                await Save(next with { Pending = next.Pending.Skip(batch.Length).ToArray(), Discord = delivery }, cancellationToken);
            }
            progress.Report(100);
        }
        finally
        {
            JobGate.Release();
        }
    }

    private Task<bool> Save(DeskState.Media state, CancellationToken ct) =>
        DeskProbes.Run(DeskProbes.StateGate, Budget, token => DeskState.SaveMedia(paths, state, token), ct);

    private MediaItem[] ReadLatest(CancellationToken ct)
    {
        var items = library.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsVirtualItem = false,
            EnableTotalRecordCount = false,
            Limit = QueryLimit,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)]
        });
        ct.ThrowIfCancellationRequested();
        return items.Select(item =>
        {
            var title = item is Episode episode ? $"{episode.SeriesName} · {episode.Name}" : item.Name;
            var clean = string.Concat((title ?? "Untitled media").Where(character => !char.IsControl(character)));
            return new MediaItem(item.Id, new DateTimeOffset(item.DateCreated.ToUniversalTime()),
                clean.Length > 100 ? clean[..97] + "…" : clean);
        }).ToArray();
    }

    private sealed record MediaItem(Guid Id, DateTimeOffset Date, string Title);
}

internal static partial class DeskDiscord
{
    internal const string DiagnosticsVariable = "RAD_DIAGNOSTICS_DISCORD_WEBHOOK";
    internal const string MediaVariable = "RAD_MEDIA_DISCORD_WEBHOOK";
    internal const string Delivered = "delivered";

    internal static string Configuration(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "missing" : IsWebhook(value) ? "configured" : "invalid";

    internal static bool IsWebhook(string value) => value.Length <= 512 && ParseWebhook(value) is not null;

    private static Uri? ParseWebhook(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == "https" && url.IsDefaultPort
        && url.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
        && url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0
        && WebhookPath().IsMatch(url.AbsolutePath) ? url : null;

    internal static async Task<string> Send(string? webhook, string content, CancellationToken ct)
    {
        var uri = ParseWebhook(webhook);
        if (uri is null) return "invalid";
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new
            {
                content, allowed_mentions = new { parse = Array.Empty<string>() },
                flags = 4 // Suppress link embeds from media names.
            })
        };
        try
        {
            using var response = await Transport.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return Delivered;
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return "rate-limited";
            return "rejected";
        }
        catch (HttpRequestException)
        {
            return "unreachable";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "timeout";
        }
    }

    // Created only on first delivery. DNS refresh and a hard request budget are independent of Jellyfin activity.
    private static class Transport
    {
        internal static HttpClient Client { get; } = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 2
        }) { Timeout = TimeSpan.FromSeconds(5) };
    }

    [GeneratedRegex(@"\A/api/webhooks/[0-9]{1,25}/[A-Za-z0-9_-]{10,200}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex WebhookPath();
}
