using Jellyfin.Plugin.RemoteAdminDesk;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

internal static class HealthDeliveryTests
{
    private const long GiB = 1024L * 1024 * 1024;

    internal static async Task Run(IApplicationPaths paths, ITaskManager tasks)
    {
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        CheckAlerts(now);

        DashboardSnapshot.VolumeDay Point(int daysAgo, long gib) =>
            new(DateOnly.FromDateTime(now.AddDays(-daysAgo).UtcDateTime), gib * GiB, now.AddDays(-daysAgo));
        var body = HealthDigest.Format(now, now.AddHours(-1),
            [new("/pool", [Point(6, 210), Point(4, 190), Point(0, 150)], 1000 * GiB),
                new("/archive", [Point(4, 100)], 1000 * GiB)],
            [new(DiagnosticIncidents.LowSpace, now.AddDays(-2), now, now)],
            [new("media", "Media scan", TaskCompletionStatus.Failed, now.AddMinutes(-20))]);
        Check(body.Contains("150 GiB free · 60 GiB less over 6 days · ~5 days to reserve")
            && body.Contains("stale") && body.Contains("1 recovered") && body.Contains("Media scan failed"),
            "Digest must include net storage change, runway, freshness, incident counts, and task status.");
        var empty = HealthDigest.Format(now, null, [], [], HealthDigest.ReadTasks(tasks));
        Check(empty.Contains("No diagnostic scan recorded") && empty.Contains("no usable readings") &&
            empty.Contains("has no recorded result"), "Empty input must stay visibly unavailable.");

        var variable = DeskDiscord.DiagnosticsVariable;
        var saved = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            var attempts = 0;
            Task<string> Send(string? _, string content, CancellationToken token)
            {
                Check(content.StartsWith("**VMS daily health digest"), "Wrong notification body.");
                return Task.FromResult(++attempts == 1 ? "unreachable" : DeskDiscord.Delivered);
            }
            Task Deliver(DateTimeOffset at) => ScheduledHealthDigest.DeliverAsync(paths, tasks, at, Send, CancellationToken.None);
            await Deliver(now);
            Check(attempts == 0, "A missing webhook must skip delivery.");
            Environment.SetEnvironmentVariable(variable, "https://discord.com/api/webhooks/123/fixture_test_token");

            try
            {
                await Deliver(now);
                throw new Exception("Failed delivery did not mark the task failed.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "Discord health digest delivery failed.") { }

            var failed = DeskState.ReadHealthDigest(paths, CancellationToken.None);
            Check(failed is { SentOn: null, Delivery.Status: "unreachable" }, "Failure must preserve retry eligibility.");
            await Deliver(now.AddMinutes(1));
            await Deliver(now.AddHours(1));
            Check(attempts == 2, "A successful digest must suppress duplicate sends that UTC day.");
            await Deliver(now.AddDays(1));
            Check(attempts == 3, "The next UTC day must be eligible.");
            Check(!new ScheduledHealthDigest(paths, tasks).GetDefaultTriggers().Any(), "Digest must be opt-in.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, saved);
        }

        Console.WriteLine("Health digest content, retry, opt-in schedule, and actionable alert throttling passed.");
    }

    private static void CheckAlerts(DateTimeOffset now)
    {
        var legacy = new DeskState.Diagnostics([], [], null, null);
        HealthDigest.TaskObservation Failed(DateTimeOffset at) =>
            new("RemoteAdminDeskMedia", "Media scan", TaskCompletionStatus.Failed, at);
        var ffmpeg = new HealthAlerts.LogSignal("one", true, now);
        var (first, firstAlerts) = HealthAlerts.Evaluate(legacy, now, true, 1, null, [ffmpeg], [Failed(now)]);
        Check(firstAlerts.Select(a => a.Key).SequenceEqual(["storage"]), "Single FFmpeg or task failure should not alert.");
        var recorded = HealthAlerts.Acknowledge(first, firstAlerts, now);
        var (noChange, suppressed) = HealthAlerts.Evaluate(legacy with { Health = recorded },
            now.AddMinutes(10), true, 1, null, [ffmpeg], [Failed(now)]);
        Check(suppressed.Length == 0, "Unchanged failures and early storage reminders should be suppressed.");
        var (more, additional) = HealthAlerts.Evaluate(legacy with { Health = noChange }, now.AddMinutes(20),
            true, 1, true, [new("two", true, now.AddMinutes(20))], [Failed(now.AddMinutes(19))]);
        Check(additional.Select(a => a.Key).SequenceEqual(["mount", "transcode", "task"]),
            "An independent mount failure and repeated task and FFmpeg signals should alert promptly.");
        var (unknown, none) = HealthAlerts.Evaluate(legacy with { Health = HealthAlerts.Acknowledge(more, additional, now.AddMinutes(20)) },
            now.AddHours(11), null, 0, null, [], []);
        Check(none.Length == 0, "Unknown probes should produce neither an alert nor a recovery.");
        var (_, due) = HealthAlerts.Evaluate(legacy with { Health = unknown }, now.AddHours(12), true, 1, false, [], []);
        Check(due.Select(a => a.Key).SequenceEqual(["storage"]), "A confirmed persistent failure may repeat at 12 hours.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
