using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.RemoteAdminDesk;

public sealed class ScheduledHealthDigest(IApplicationPaths paths, ITaskManager taskManager) : IScheduledTask
{
    private static readonly SemaphoreSlim DeliveryGate = new(1, 1);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    internal const string TaskKey = "RemoteAdminDeskHealthDigest";
    public string Key => TaskKey;
    public string Name => "VMS daily health digest";
    public string Description => "Send a daily summary of storage, VMS task results, and incidents to the diagnostics Discord webhook.";
    public string Category => Plugin.DisplayName;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!DeliveryGate.Wait(0, cancellationToken)) return;
        try
        {
            await DeliverAsync(paths, taskManager, DateTimeOffset.UtcNow, DeskDiscord.Send, cancellationToken);
            progress.Report(100);
        }
        finally
        {
            DeliveryGate.Release();
        }
    }

    // The scheduled task owns the delivery gate while this operation is in flight.
    internal static async Task DeliverAsync(IApplicationPaths paths, ITaskManager taskManager, DateTimeOffset now,
        Func<string?, string, CancellationToken, Task<string>> send, CancellationToken ct)
    {
        var (previous, webhook, content) = await DeskProbes.Run<(DeskState.HealthDigestDelivery?, string?, string?)>(DeskProbes.StateGate, Budget, token =>
        {
            var delivery = DeskState.ReadHealthDigest(paths, token);
            var settings = DeskSettings.Read(paths.DataPath, token);
            var url = DeskSettings.Effective(DeskDiscord.DiagnosticsVariable, settings.DiagnosticsWebhook);
            if (string.IsNullOrWhiteSpace(url) || delivery?.SentOn >= DateOnly.FromDateTime(now.UtcDateTime))
                return (delivery, url, null);

            var diagnostics = DeskState.ReadDiagnostics(paths, token);
            var checks = DashboardSnapshot.ReadChecks(paths, token);
            var samples = DashboardSnapshot.MergeStorageHistory(checks, diagnostics);
            return (delivery, url, HealthDigest.Format(now, diagnostics.LastScanAt,
                DashboardSnapshot.CapacityHistory(samples, now), DiagnosticIncidents.Read(paths, token),
                HealthDigest.ReadTasks(taskManager)));
        }, ct, waitForSlot: true);
        if (content is null) return;

        var status = await send(webhook, content, ct);
        var delivery = DeskState.RecordDelivery(previous?.Delivery, status);
        var sentOn = status == DeskDiscord.Delivered ? DateOnly.FromDateTime(now.UtcDateTime) : previous?.SentOn;
        await DeskProbes.Run(DeskProbes.StateGate, Budget, token =>
            DeskState.SaveHealthDigest(paths, new DeskState.HealthDigestDelivery(sentOn, delivery), token),
            status == DeskDiscord.Delivered ? CancellationToken.None : ct, waitForSlot: true);
        if (status != DeskDiscord.Delivered)
            throw new InvalidOperationException("Discord health digest delivery failed.");
    }
}

internal static class HealthDigest
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(30);

    internal sealed record TaskObservation(string Key, string Label, TaskCompletionStatus? Status, DateTimeOffset? FinishedAt);

    internal static TaskObservation[] ReadTasks(ITaskManager manager)
    {
        var workers = manager.ScheduledTasks;
        return new (string Key, string Label)[]
        {
            ("RemoteAdminDeskScan", "Diagnostics"),
            ("RemoteAdminDeskMedia", "Media scan"),
            ("RemoteAdminDeskVisual", "Visual refresh")
        }.Select(task =>
        {
            var result = workers.FirstOrDefault(worker => worker.ScheduledTask.Key == task.Key)?.LastExecutionResult;
            return new TaskObservation(task.Key, task.Label, result?.Status,
                result is null ? null : new DateTimeOffset(result.EndTimeUtc.ToUniversalTime()));
        }).ToArray();
    }

    internal static string Format(DateTimeOffset now, DateTimeOffset? lastScan,
        DashboardSnapshot.Capacity[] volumes, DiagnosticIncidents.Incident[] incidents, TaskObservation[] tasks)
    {
        var since = now.AddDays(-1);
        var open = incidents.Count(incident => incident.ResolvedAt is null);
        var resolved = incidents.Count(incident => incident.ResolvedAt is { } at && at > since && at <= now);
        var opened = incidents.Count(incident => incident.OpenedAt > since && incident.OpenedAt <= now);
        var lines = new List<string>
        {
            FormattableString.Invariant($"**VMS daily health digest · {now.UtcDateTime:yyyy-MM-dd} UTC**"),
            lastScan is { } checkedAt
                ? "Last diagnostic scan: " + Stamp(checkedAt) + (now - checkedAt > StaleAfter ? " (stale)" : "")
                : "No diagnostic scan recorded.",
            FormattableString.Invariant($"Incidents: {open} open · {opened} opened · {resolved} recovered in the last 24h.")
        };
        foreach (var kind in incidents.Where(incident => incident.ResolvedAt is null)
            .Select(incident => DiagnosticIncidents.Label(incident.Kind)).Distinct())
            lines.Add("• " + kind);

        if (volumes.Length == 0) lines.Add("Storage: no usable readings.");
        foreach (var volume in volumes.OrderByDescending(volume => volume.TotalBytes).Take(4))
            lines.Add("• " + StorageLine(volume, now));
        if (volumes.Length > 4)
            lines.Add(FormattableString.Invariant($"• {volumes.Length - 4} more tracked volume(s) in the dashboard."));

        lines.Add("Tasks: " + string.Join(" · ", tasks.Select(task => task.Label + " "
            + (task.Status switch
            {
                TaskCompletionStatus.Completed => "completed",
                TaskCompletionStatus.Failed => "failed",
                TaskCompletionStatus.Cancelled => "cancelled",
                TaskCompletionStatus.Aborted => "aborted",
                _ => "has no recorded result"
            }) + (task.FinishedAt is { } finishedAt ? " (" + Stamp(finishedAt) + ")" : ""))));
        return string.Join("\n", lines);
    }

    internal static double Reserve(long total) => Math.Min(0.15 * total, Math.Max(30d * GiB, 0.10 * total));

    private static string StorageLine(DashboardSnapshot.Capacity volume, DateTimeOffset now)
    {
        var latest = volume.Days[^1];
        var mount = string.Concat(volume.Mount.Where(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_').Take(48));
        var summary = (mount.Length > 0 ? $"`{mount}`" : "Volume") + ": " + Size(latest.FreeBytes) + " free";
        if (latest.SampledAt is not { } lastAt || now - lastAt > StaleAfter)
            return summary + " (stale" + (latest.SampledAt is { } at ? ", " + Stamp(at) : "") + ")";

        var total = volume.TotalBytes!.Value; // CapacityHistory emits only valid measured volumes.
        var reserve = Reserve(total);
        if (latest.FreeBytes <= reserve) return summary + " · at or below reserve";

        var points = volume.Days.Where(day => day.SampledAt is { } at && lastAt - at <= TimeSpan.FromDays(7)).ToArray();
        var baseline = 0;
        for (var i = 1; i < points.Length; i++)
        {
            if (points[i].FreeBytes - points[i - 1].FreeBytes > Math.Max(10 * GiB, total * 0.02))
                baseline = i;
        }
        var first = points[baseline];
        var span = (lastAt - first.SampledAt!.Value).TotalDays;
        if (points.Length - baseline < 3 || span < 4)
            return summary + " · runway unavailable";

        var loss = first.FreeBytes - latest.FreeBytes;
        var direction = loss > 0 ? "less" : loss < 0 ? "more" : "change";
        var change = FormattableString.Invariant($" · {Size(Math.Abs(loss))} {direction} over {span:0.#} days");
        if (loss <= 0) return summary + change + " · no net space loss";
        var runway = (latest.FreeBytes - reserve) / (loss / span);
        return summary + change + (runway > 365 ? " · >365 days to reserve"
            : FormattableString.Invariant($" · ~{Math.Ceiling(runway)} days to reserve"));
    }

    private static string Stamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("MMM d HH:mm 'UTC'", CultureInfo.InvariantCulture);
    private static string Size(long bytes) => bytes >= 1024 * GiB
        ? FormattableString.Invariant($"{(double)bytes / (1024 * GiB):0.##} TiB")
        : FormattableString.Invariant($"{(double)bytes / GiB:0.##} GiB");
}
