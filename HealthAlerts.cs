using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.RemoteAdminDesk;

internal static class HealthAlerts
{
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan TranscodeWindow = TimeSpan.FromHours(1);

    internal sealed record LogSignal(string Fingerprint, bool IsTranscode, DateTimeOffset ModifiedAt);
    internal sealed record TaskStreak(string Key, DateTimeOffset FinishedAt, int Failures);
    internal sealed record State(Dictionary<string, DateTimeOffset> LastSent, LogSignal[] RecentTranscodes, TaskStreak[] Tasks);
    internal sealed record Notice(string Key, string Text);

    internal static (State State, Notice[] Notices) Evaluate(DeskState.Diagnostics previous, DateTimeOffset now,
        bool? lowSpace, int lowVolumes, bool? missingMount, LogSignal[] logSignals, HealthDigest.TaskObservation[] tasks)
    {
        var prior = previous.Health ?? new State([], [], []);
        var recent = prior.RecentTranscodes.Concat(logSignals.Where(signal => signal.IsTranscode))
            .Where(signal => signal.ModifiedAt > now - TranscodeWindow && signal.ModifiedAt <= now)
            .OrderByDescending(signal => signal.ModifiedAt).DistinctBy(signal => signal.Fingerprint).Take(12).ToArray();

        var oldTasks = prior.Tasks.ToDictionary(task => task.Key, StringComparer.Ordinal);
        var streaks = new List<TaskStreak>();
        var failed = new List<string>();
        foreach (var task in tasks.Where(task => task.Key != "RemoteAdminDeskScan"))
        {
            var streak = oldTasks.GetValueOrDefault(task.Key);
            if (task.FinishedAt is { } finished && finished <= now && (streak is null || finished > streak.FinishedAt))
                streak = new TaskStreak(task.Key, finished,
                    task.Status == TaskCompletionStatus.Failed ? Math.Min(2, (streak?.Failures ?? 0) + 1) : 0);
            if (streak is null) continue;
            streaks.Add(streak);
            if (streak.Failures >= 2 && task.Status == TaskCompletionStatus.Failed) failed.Add(task.Label);
        }

        var notices = new List<Notice>();
        void Add(string key, string message)
        {
            if (!prior.LastSent.TryGetValue(key, out var sent) || now - sent >= RepeatInterval)
                notices.Add(new Notice(key, message));
        }

        if (missingMount == true)
            Add("mount", "A media mount is unavailable. Check the expected mounts in VMS.");
        if (lowSpace == true)
            Add("storage", $"{lowVolumes} media volume(s) are at or below the free-space reserve. Check Free Space and Runway.");
        if (logSignals.Any(signal => !signal.IsTranscode && !previous.Delivered.Contains(signal.Fingerprint, StringComparer.Ordinal)))
            Add("server-log", "A recent Jellyfin log contains a serious server failure. Check recent server logs.");
        if (recent.Length >= 2)
            Add("transcode", $"{recent.Length} distinct FFmpeg failure signals were observed in the past hour. Check playback troubleshooting.");
        if (failed.Count > 0)
            Add("task", "Repeated task failures observed: " + string.Join(", ", failed) + ". Check Jellyfin Scheduled Tasks.");

        return (prior with { RecentTranscodes = recent, Tasks = streaks.ToArray() }, notices.ToArray());
    }

    internal static State Acknowledge(State next, Notice[] notices, DateTimeOffset sentAt)
    {
        var delivered = new Dictionary<string, DateTimeOffset>(next.LastSent, StringComparer.Ordinal);
        foreach (var notice in notices) delivered[notice.Key] = sentAt;
        return next with { LastSent = delivered };
    }
}
