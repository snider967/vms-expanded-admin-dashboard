using System.Net.Http.Json;
using System.Text.Json;
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
    ILibraryManager library, IApplicationPaths paths, ILogger<ScheduledDiagnostics> logger, ITaskManager taskManager) : IScheduledTask
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
                ct => (DeskState.ReadDiagnostics(paths, ct), DeskSettings.Read(paths.DataPath, ct)),
                cancellationToken, waitForSlot: true);
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
            var lowObservation = low.Length > 0 ? (bool?)true
                : readings.Length > 0 && readings.Length == storage.Volumes.Length
                    && !storage.Limited && storage.Locations.All(location => location.Mount is not null)
                    ? false : null;
            var configuredBranches = DeskSettings.Effective("RAD_MEDIA_BRANCHES", settings.MediaBranches);
            var branches = await DeskProbes.Run(DeskProbes.StorageGate, ProbeBudget,
                ct => new DashboardSnapshot.TimedBranches(DateTimeOffset.UtcNow, _probes.ReadBranches(configuredBranches, ct)),
                cancellationToken);
            var mountObservation = storage.Locations.Any(location => location.Problem is not null)
                || storage.Volumes.Any(volume => volume.Problem is not null)
                || branches.Result.Branches.Any(volume => volume.Problem is not null) ? true
                : !storage.Limited && storage.Problem is null && storage.Locations.Length > 0
                    && (string.IsNullOrWhiteSpace(configuredBranches) || (!branches.Result.Limited && branches.Result.Problem is null))
                    ? false : (bool?)null;
            await RecordIncidents(branches.SampledAt, lowObservation, mountObservation, cancellationToken);

            await DeskProbes.Run(DeskProbes.StateGate, ProbeBudget,
                ct => DashboardSnapshot.UpdateChecks(paths, ct, storage, branches), cancellationToken, waitForSlot: true);

            var now = DateTimeOffset.UtcNow;
            var (health, notices) = HealthAlerts.Evaluate(state, now, lowObservation, low.Length, mountObservation, signals,
                HealthDigest.ReadTasks(taskManager));
            var delivery = state.Discord;
            var sent = false;
            if (notices.Length > 0 && !string.IsNullOrWhiteSpace(webhook))
            {
                var result = await DeskDiscord.Send(webhook,
                    "**VMS diagnostic alert**\n" + string.Join("\n", notices.Select(notice => "• " + notice.Text)), cancellationToken);
                delivery = DeskState.RecordDelivery(delivery, result);
                if (result != DeskDiscord.Delivered)
                {
                    // Preserve the previous alert checkpoint so the same evidence can be retried.
                    await Save(state with { Discord = delivery }, cancellationToken);
                    logger.LogWarning("VMS Expanded Admin Dashboard could not deliver its Discord diagnostic notification.");
                    throw new InvalidOperationException("Discord diagnostic delivery failed.");
                }
                sent = true;
                health = HealthAlerts.Acknowledge(health, notices, delivery.DeliveredAt!.Value);
            }

            var delivered = state.Delivered.Concat(signals.Select(signal => signal.Fingerprint)).Distinct().TakeLast(64).ToArray();
            await Save(new DeskState.Diagnostics(samples, delivered, lowObservation ?? state.StorageWarning, now, delivery,
                mountObservation ?? state.MountWarning, health), sent ? CancellationToken.None : cancellationToken);
            progress.Report(100);
        }
        finally
        {
            ScanGate.Release();
        }
    }

    private async Task RecordIncidents(DateTimeOffset observedAt, bool? lowSpace, bool? missingMount, CancellationToken ct)
    {
        try
        {
            await DeskProbes.Run(DeskProbes.StateGate, ProbeBudget, token =>
            {
                DiagnosticIncidents.Record(paths, observedAt, lowSpace, missingMount, token);
                return true;
            }, ct, waitForSlot: true);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or DeskProbeBusyException
            || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("VMS Expanded Admin Dashboard could not save diagnostic incident history.");
        }
    }

    private Task<bool> Save(DeskState.Diagnostics state, CancellationToken ct) =>
        DeskProbes.Run(DeskProbes.StateGate, ProbeBudget, token => DeskState.SaveDiagnostics(paths, state, token),
            ct, waitForSlot: true);

    private static bool IsLow(DeskProbes.Volume volume)
    {
        return volume.Available!.Value <= HealthDigest.Reserve(volume.Total!.Value);
    }

    private static DeskState.Sample[] AddDailySample(DeskState.Sample[] samples, DeskProbes.StorageReport report)
    {
        var usable = report.Volumes.Where(v => v.Total.HasValue && v.Available.HasValue).ToArray();
        if (usable.Length == 0) return samples;
        return samples.Where(sample => sample.SampledAt.UtcDateTime.Date != report.SampledAt.UtcDateTime.Date)
            .Append(new DeskState.Sample(report.SampledAt, usable)).TakeLast(120).ToArray();
    }

    private HealthAlerts.LogSignal[] ReadFailures(DateTimeOffset? previousScan, CancellationToken ct)
    {
        if (previousScan is null) return [];
        var catalog = _probes.ReadCatalog(ct);
        var recent = catalog.Files.Where(file => file.ModifiedAt >= previousScan.Value.AddMinutes(-2)).ToArray();
        var server = recent.Where(file => !file.Name.StartsWith("FFmpeg", StringComparison.OrdinalIgnoreCase)).Take(1);
        var ffmpeg = recent.Where(file => file.Name.StartsWith("FFmpeg", StringComparison.OrdinalIgnoreCase)).Take(3);
        var failures = new List<HealthAlerts.LogSignal>();
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
                failures.Add(new HealthAlerts.LogSignal(fingerprint, ff, file.ModifiedAt));
            }
            catch (FileNotFoundException)
            {
                // Ignore a log that rotated while the scan was in progress.
            }
        }
        return failures.ToArray();
    }

    [GeneratedRegex(@"no space left on device|disk quota exceeded|database disk image is malformed|outofmemoryexception|input/output error|ffmpeg exited with code [1-9]|error while opening encoder|error initializing output stream|conversion failed|device creation failed|no va display|failed to initialize (?:vaapi|qsv|cuda)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SignificantFailure();
}

public sealed class ScheduledMediaAlerts(
    ILibraryManager library, IApplicationPaths paths, ILogger<ScheduledMediaAlerts> logger) : IScheduledTask
{
    private const int QueryLimit = 120;
    private const int MaxPagesPerRun = 8;
    private const int MaxMessagesPerRun = 12;
    private const int MessageItems = 14;
    private const int DiscordContentLimit = 2000;
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
                ct => (DeskState.ReadMedia(paths, ct), DeskSettings.Read(paths.DataPath, ct)),
                cancellationToken, waitForSlot: true);
            var webhook = DeskSettings.Effective(DeskDiscord.MediaVariable, settings.MediaWebhook);
            var sampledAt = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(webhook))
            {
                logger.LogInformation("VMS Expanded Admin Dashboard media alerts have no Discord webhook configured.");
                progress.Report(100);
                return;
            }
            progress.Report(60);
            var state = previous ?? new DeskState.Media([], null, [], sampledAt, Cursor: sampledAt);
            state = await FlushReceipt(state, cancellationToken);
            if (state.Cursor is null)
            {
                // Migrate the recorded scan cutoff and retain the old IDs and pending messages.
                state = state with { Cursor = state.LastScanAt ?? sampledAt };
            }
            if (state.Digest is null)
            {
                state = state with
                {
                    Digest = new DeskState.MediaDigest(Guid.NewGuid()),
                    ScanThrough = state.ScanThrough ?? sampledAt
                };
                await Save(state, cancellationToken);
            }
            if (state.Digest.Pages == 0 && state.LegacyQueuedCount > 0)
                state = await StageLegacy(state, cancellationToken);

            // Seal the full window before sending its first message. Later runs
            // resume either the scan or delivery using the same durable digest.
            for (var page = 0; page < MaxPagesPerRun && state.ScanThrough is not null; page++)
                state = await ScanPage(state, cancellationToken);
            if (state.ScanThrough is null)
            {
                for (var message = 0; message < MaxMessagesPerRun && state.QueuedCount > 0; message++)
                    state = await Deliver(state, webhook, cancellationToken);
                if (state.QueuedCount == 0)
                    await Save(state with { Digest = null }, cancellationToken);
            }
            progress.Report(100);
        }
        finally
        {
            JobGate.Release();
        }
    }

    private async Task<DeskState.Media> ScanPage(DeskState.Media state, CancellationToken ct)
    {
        var cutoff = state.Cursor!.Value;
        var through = state.ScanThrough!.Value;
        var boundary = state.CursorIds ?? [];
        var page = await DeskProbes.Run(QueryGate, Budget, token =>
            ReadItems(token, cutoff, boundary), ct);
        var items = page.Where(item => item.Date <= through).ToArray();
        var complete = page.Length < QueryLimit || items.Length < page.Length;

        // Advance the cursor only alongside the queued page. Excluding IDs at the
        // inclusive boundary avoids offset races and handles equal timestamps.
        var cursor = complete ? through : items[^1].Date;
        var cursorIds = (cursor == cutoff ? boundary : []).Concat(
            items.Where(item => item.Date == cursor).Select(item => item.Id)).Distinct().ToArray();
        if (cursorIds.Length > DeskState.MediaBoundaryLimit)
            throw new IOException("The media scan has too many items at one timestamp.");
        var legacy = state.Known.Concat((state.Outbox ?? []).Select(item => item.Id)).ToHashSet();
        var alerts = items.Where(item => !legacy.Contains(item.Id))
            .Select(item => new DeskState.MediaAlert(item.Id, item.Title)).DistinctBy(item => item.Id).ToArray();
        var next = state with
        {
            Cursor = cursor, CursorIds = cursorIds, ScanThrough = complete ? null : through,
            LastScanAt = complete ? through : state.LastScanAt,
            Outbox = complete ? null : state.Outbox
        };
        return await StagePage(next, alerts, ct);
    }

    private Task<DeskState.Media> StageLegacy(DeskState.Media state, CancellationToken ct)
    {
        var alerts = state.Pending.Select(title => new DeskState.MediaAlert(Guid.Empty, title))
            .Concat((state.Outbox ?? []).DistinctBy(item => item.Id)).ToArray();
        return StagePage(state with { Pending = [] }, alerts, ct);
    }

    private Task<DeskState.Media> StagePage(DeskState.Media state, DeskState.MediaAlert[] alerts, CancellationToken ct) =>
        DeskProbes.Run(DeskProbes.StateGate, Budget, token =>
        {
            var delivered = DeskState.ReadDeliveredMedia(paths,
                alerts.Where(item => item.Id != Guid.Empty).Select(item => item.Id), token);
            var pending = alerts.Where(item => !delivered.Contains(item.Id)).ToArray();
            var digest = state.Digest!;
            if (pending.Length > 0)
            {
                // Write the page before advancing the checkpoint. If interrupted,
                // the same page index can be overwritten on the next scan.
                DeskState.SaveDigestPage(paths, digest, pending, token);
                state = state with { Digest = digest with { Pages = digest.Pages + 1, Total = digest.Total + pending.Length } };
            }
            DeskState.SaveMedia(paths, state, token);
            return state;
        }, ct, waitForSlot: true);

    private async Task<DeskState.Media> Deliver(DeskState.Media state, string webhook, CancellationToken ct)
    {
        var digest = state.Digest!;
        var page = await DeskProbes.Run(DeskProbes.StateGate, Budget,
            token => DeskState.ReadDigestPage(paths, digest, token), ct, waitForSlot: true);
        var additions = digest.Total == 1 ? "addition" : "additions";
        var content = digest.Sent == 0 ? $"## New in Jellyfin · {digest.Total} {additions}\n\n" : "";
        var take = 0;
        foreach (var item in page.Skip(digest.PageOffset).Take(MessageItems))
        {
            var title = CleanTitle(item.Title);
            var line = "• " + string.Concat(title.Select(character =>
                @"\`*_~|[]>".Contains(character) ? "\\" + character : character.ToString()));
            if (content.Length + line.Length + 1 > DiscordContentLimit) break;
            if (take > 0) content += "\n";
            content += line;
            take++;
        }
        if (take == 0) throw new IOException("The queued media digest page is invalid.");
        var offset = digest.PageOffset + take;
        var finishedPage = offset == page.Length;

        var status = await DeskDiscord.Send(webhook, content, ct);
        var delivery = DeskState.RecordDelivery(state.Discord, status);
        if (status != DeskDiscord.Delivered)
        {
            await Save(state with { Discord = delivery }, ct);
            logger.LogWarning("VMS Expanded Admin Dashboard could not deliver its Discord media notification.");
            throw new InvalidOperationException("Discord media delivery failed.");
        }

        // Discord has acknowledged the request. Finish recording it even if the
        // scheduled task was cancelled while that response was in flight.
        var next = state with
        {
            Discord = delivery,
            DeliveryReceipt = page.Skip(digest.PageOffset).Take(take).Select(item => item.Id)
                .Where(id => id != Guid.Empty).ToArray(),
            Digest = digest with
            {
                Sent = digest.Sent + take,
                NextPage = finishedPage ? digest.NextPage + 1 : digest.NextPage,
                PageOffset = finishedPage ? 0 : offset
            }
        };
        await Save(next, CancellationToken.None);
        if (finishedPage)
        {
            try { DeskState.RemoveDigestPage(paths, digest); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("VMS could not clean up a delivered media digest page.");
            }
        }
        return await FlushReceipt(next, CancellationToken.None);
    }

    private async Task<DeskState.Media> FlushReceipt(DeskState.Media state, CancellationToken ct)
    {
        if (state.DeliveryReceipt is not { Length: > 0 } receipt) return state;
        var next = state with { DeliveryReceipt = null };
        await DeskProbes.Run(DeskProbes.StateGate, Budget, token =>
        {
            DeskState.RecordDeliveredMedia(paths, receipt, token);
            return DeskState.SaveMedia(paths, next, token);
        }, ct, waitForSlot: true);
        return next;
    }

    private Task<bool> Save(DeskState.Media state, CancellationToken ct) =>
        DeskProbes.Run(DeskProbes.StateGate, Budget, token => DeskState.SaveMedia(paths, state, token),
            ct, waitForSlot: true);

    private MediaItem[] ReadItems(CancellationToken ct, DateTimeOffset cutoff, Guid[] excluded)
    {
        var items = library.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsVirtualItem = false,
            EnableTotalRecordCount = false,
            Limit = QueryLimit,
            MinDateCreated = cutoff.UtcDateTime,
            ExcludeItemIds = excluded,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Ascending)]
        });
        ct.ThrowIfCancellationRequested();
        return items.Select(item =>
        {
            var title = item is Episode episode ? $"{episode.SeriesName} · {episode.Name}" : item.Name;
            return new MediaItem(item.Id, new DateTimeOffset(item.DateCreated.ToUniversalTime()), CleanTitle(title));
        }).ToArray();
    }

    private static string CleanTitle(string? title)
    {
        var clean = string.Concat((title ?? "").Where(character => !char.IsControl(character))).Trim();
        if (clean.Length == 0) return "Untitled media";
        if (clean.Length <= 100) return clean;
        return clean[..(char.IsHighSurrogate(clean[96]) ? 96 : 97)] + "…";
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
        using var request = new HttpRequestMessage(HttpMethod.Post, new UriBuilder(uri) { Query = "wait=true" }.Uri)
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
