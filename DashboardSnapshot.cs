using Jellyfin.Data.Queries;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.RemoteAdminDesk;

internal static class DashboardSnapshot
{
    private const string ChecksFile = "dashboard-checks.json";
    private const string ChartsFile = "dashboard-charts.json";
    internal const int HistoryDays = 90;
    internal sealed record TimedBranches(DateTimeOffset SampledAt, DeskProbes.BranchReport Result);
    internal sealed record MediaDay(DateOnly Date, int Movies, int Episodes);
    internal sealed record MediaHour(int Hour, int Movies, int Episodes);
    internal sealed record MediaHistory(DateTimeOffset SampledAt, DateTimeOffset? CoverageStart, MediaDay[] Days,
        MediaHour[]? Hours = null);
    internal sealed record ActivityDay(DateOnly Date, int Starts, int SignIns);
    internal sealed record ActivityHour(int Hour, int Starts, int SignIns);
    internal sealed record VolumeDay(DateOnly Date, long FreeBytes, DateTimeOffset? SampledAt = null);
    internal sealed record Capacity(string Mount, VolumeDay[] Days, long? TotalBytes = null, bool BaselineChanged = false);
    internal sealed record Checks(DeskProbes.StorageReport? Storage = null, TimedBranches? Branches = null,
        MediaHistory? Media = null, DeskState.Sample[]? StorageHistory = null);
    internal sealed record Charts(DateTimeOffset SampledAt, DateTimeOffset? ActivityCoverageStart, ActivityDay[] Activity,
        MediaHistory? Media, Capacity[] Capacity, ActivityHour[]? ActivityHours = null,
        DateTimeOffset? OldestRecordedActivity = null);

    internal static int HourIndex(DateTimeOffset value, DateTimeOffset sampledAt) =>
        Math.Min(23, (int)Math.Floor((value - sampledAt.AddHours(-24)).TotalHours));

    internal static Checks ReadChecks(IApplicationPaths paths, CancellationToken ct) =>
        DeskState.Read<Checks>(paths, ChecksFile, ct) ?? new Checks();

    internal static Charts? ReadCharts(IApplicationPaths paths, CancellationToken ct) =>
        DeskState.Read<Charts>(paths, ChartsFile, ct);

    // The caller holds StateGate across each read, merge and write.
    internal static bool UpdateChecks(IApplicationPaths paths, CancellationToken ct,
        DeskProbes.StorageReport? storage = null, TimedBranches? branches = null, MediaHistory? media = null)
    {
        var current = ReadChecks(paths, ct);
        if (storage is { Problem: null })
        {
            var volumes = storage.Volumes.Where(volume => volume.Total > 0 && volume.Available.HasValue).ToArray();
            var history = current.StorageHistory ?? [];
            var day = storage.SampledAt.UtcDateTime.Date;
            if (volumes.Length > 0 && !history.Any(sample => sample.SampledAt.UtcDateTime.Date == day
                && sample.SampledAt >= storage.SampledAt))
            {
                history = history.Where(sample => sample.SampledAt.UtcDateTime.Date != day)
                    .Append(new DeskState.Sample(storage.SampledAt, volumes))
                    .OrderBy(sample => sample.SampledAt).TakeLast(120).ToArray();
                current = current with { StorageHistory = history };
            }
            if (current.Storage is null || current.Storage.SampledAt < storage.SampledAt)
                current = current with { Storage = storage };
        }
        if (branches is not null && branches.Result.Problem is null
            && (current.Branches is null || current.Branches.SampledAt < branches.SampledAt))
            current = current with { Branches = branches };
        if (media is not null && (current.Media is null || current.Media.SampledAt < media.SampledAt))
            current = current with { Media = media };
        return DeskState.Save(paths, ChecksFile, current, ct);
    }

    internal static DeskState.Sample[] MergeStorageHistory(Checks checks, DeskState.Diagnostics diagnostics) =>
        diagnostics.Samples.Concat(checks.StorageHistory ?? [])
            .GroupBy(sample => sample.SampledAt.UtcDateTime.Date)
            .Select(day => day.MaxBy(sample => sample.SampledAt)!)
            .OrderBy(sample => sample.SampledAt).TakeLast(120).ToArray();

    internal static Capacity[] CapacityHistory(DeskState.Sample[] samples, DateTimeOffset through) =>
        samples.Where(sample => sample.SampledAt <= through
                && sample.SampledAt.UtcDateTime.Date >= through.UtcDateTime.Date.AddDays(1 - HistoryDays))
            .SelectMany(sample => sample.Volumes.Select(volume => (sample.SampledAt, Volume: volume)))
            .Where(point => point.Volume is { Available: >= 0, Total: > 0, Problem: null }
                && point.Volume.Available <= point.Volume.Total)
            .GroupBy(point => point.Volume.Mount)
            .OrderByDescending(series => series.Max(point => point.SampledAt)).ThenBy(series => series.Key, StringComparer.Ordinal)
            .Take(8)
            .Select(series =>
            {
                var points = series.OrderBy(point => point.SampledAt).ToArray();
                var latest = points[^1].Volume;
                var baseline = Array.FindLastIndex(points, point => point.Volume.Device != latest.Device
                    || point.Volume.FileSystem != latest.FileSystem || point.Volume.Total != latest.Total);
                return new Capacity(series.Key, points.Skip(baseline + 1)
                    .Select(point => new VolumeDay(DateOnly.FromDateTime(point.SampledAt.UtcDateTime),
                        point.Volume.Available!.Value, point.SampledAt)).ToArray(), latest.Total, baseline >= 0);
            }).ToArray();

    internal static bool SaveCharts(IApplicationPaths paths, Charts charts, CancellationToken ct) =>
        DeskState.Save(paths, ChartsFile, charts, ct);
}

public sealed class UpdateVisualDashboard(IActivityManager activityManager, IApplicationPaths paths) : IScheduledTask
{
    private const int ActivityLimit = 500;
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    public string Name => "Update visual dash";
    public string Key => "RemoteAdminDeskVisual";
    public string Description => "Refresh the Overview charts from recorded account activity and saved media and storage samples.";
    public string Category => Plugin.DisplayName;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await DeskProbes.RunAsync(RefreshGate, Budget, async ct =>
        {
            var now = DateTimeOffset.UtcNow;
            var since = now.UtcDateTime.Date.AddDays(1 - DashboardSnapshot.HistoryDays);
            var result = await activityManager.GetPagedResultAsync(new ActivityLogQuery
            {
                HasUserId = true,
                MinDate = since,
                Limit = ActivityLimit
            });
            ct.ThrowIfCancellationRequested();
            var entries = result.Items.Where(entry => entry.Date.ToUniversalTime() <= now.UtcDateTime).ToArray();
            var activity = entries.Where(entry => IsPlaybackStart(entry.Type) || entry.Type == "AuthenticationSucceeded").ToArray();
            var history = activity.GroupBy(entry => DateOnly.FromDateTime(entry.Date.ToUniversalTime()))
                .Select(day => new DashboardSnapshot.ActivityDay(day.Key,
                    day.Count(entry => IsPlaybackStart(entry.Type)),
                    day.Count(entry => entry.Type == "AuthenticationSucceeded")))
                .OrderBy(day => day.Date).ToArray();
            var hours = activity.GroupBy(entry => DashboardSnapshot.HourIndex(entry.Date.ToUniversalTime(), now))
                .Where(hour => hour.Key >= 0)
                .Select(hour => new DashboardSnapshot.ActivityHour(hour.Key,
                    hour.Count(entry => IsPlaybackStart(entry.Type)),
                    hour.Count(entry => entry.Type == "AuthenticationSucceeded")))
                .OrderBy(hour => hour.Hour).ToArray();
            DateTimeOffset? oldest = entries.Length == 0 ? null : entries.Min(entry => entry.Date.ToUniversalTime());
            var capped = result.Items.Count >= ActivityLimit || result.TotalRecordCount > result.Items.Count;
            return await DeskProbes.Run(DeskProbes.StateGate, Budget, token =>
            {
                var previous = DashboardSnapshot.ReadCharts(paths, token);
                if (previous is not null && previous.SampledAt >= now) return true;
                var checks = DashboardSnapshot.ReadChecks(paths, token);
                var storageHistory = DashboardSnapshot.MergeStorageHistory(checks, DeskState.ReadDiagnostics(paths, token));
                var charts = new DashboardSnapshot.Charts(now, capped ? oldest ?? now : null, history, checks.Media,
                    DashboardSnapshot.CapacityHistory(storageHistory, now), hours, oldest);
                return DashboardSnapshot.SaveCharts(paths, charts, token);
            }, ct, waitForSlot: true);
        }, cancellationToken);
        progress.Report(100);
    }

    private static bool IsPlaybackStart(string? type) => type is "VideoPlayback" or "AudioPlayback" or "GamePlayback";
}
