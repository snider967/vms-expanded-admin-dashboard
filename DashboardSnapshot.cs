using Jellyfin.Data.Queries;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.RemoteAdminDesk;

internal static class DashboardSnapshot
{
    private const string ChecksFile = "dashboard-checks.json";
    private const string ChartsFile = "dashboard-charts.json";
    internal sealed record TimedBranches(DateTimeOffset SampledAt, DeskProbes.BranchReport Result);
    internal sealed record MediaDay(DateOnly Date, int Movies, int Episodes);
    internal sealed record MediaHistory(DateTimeOffset SampledAt, DateTimeOffset? CoverageStart, MediaDay[] Days);
    internal sealed record ActivityDay(DateOnly Date, int Starts, int SignIns);
    internal sealed record VolumeDay(DateOnly Date, long FreeBytes);
    internal sealed record Capacity(string Mount, VolumeDay[] Days);
    internal sealed record Checks(DeskProbes.StorageReport? Storage = null, TimedBranches? Branches = null,
        MediaHistory? Media = null, DeskState.Sample[]? StorageHistory = null);
    internal sealed record Charts(DateTimeOffset SampledAt, DateTimeOffset? ActivityCoverageStart, ActivityDay[] Activity,
        MediaHistory? Media, Capacity[] Capacity);

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

    internal static Capacity[] CapacityHistory(DeskState.Sample[] samples) =>
        samples.SelectMany(sample => sample.Volumes.Select(volume => (sample.SampledAt, Volume: volume)))
            .Where(point => point.Volume.Available.HasValue)
            .GroupBy(point => point.Volume.Mount)
            .OrderByDescending(series => series.Max(point => point.SampledAt)).ThenBy(series => series.Key, StringComparer.Ordinal)
            .Take(8)
            .Select(series =>
            {
                var latest = series.Last().Volume;
                return new Capacity(series.Key, series.Where(point => point.Volume.Device == latest.Device
                        && point.Volume.FileSystem == latest.FileSystem && point.Volume.Total == latest.Total)
                    .TakeLast(30)
                    .Select(point => new VolumeDay(DateOnly.FromDateTime(point.SampledAt.UtcDateTime), point.Volume.Available!.Value))
                    .ToArray());
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
        var (now, history, coverage) = await DeskProbes.RunAsync(RefreshGate, Budget, async ct =>
        {
            var now = DateTimeOffset.UtcNow;
            var since = now.UtcDateTime.Date.AddDays(-89);
            var result = await activityManager.GetPagedResultAsync(new ActivityLogQuery
            {
                HasUserId = true,
                MinDate = since,
                Limit = ActivityLimit
            });
            ct.ThrowIfCancellationRequested();
            var entries = result.Items;
            var history = entries.GroupBy(entry => DateOnly.FromDateTime(entry.Date.ToUniversalTime()))
                .Select(day => new DashboardSnapshot.ActivityDay(day.Key,
                    day.Count(entry => entry.Type is "VideoPlayback" or "AudioPlayback" or "GamePlayback"),
                    day.Count(entry => entry.Type == "AuthenticationSucceeded")))
                .OrderBy(day => day.Date).ToArray();
            DateTimeOffset? coverage = entries.Count > 0 && (entries.Count >= ActivityLimit || result.TotalRecordCount > entries.Count)
                ? entries.Min(entry => entry.Date.ToUniversalTime()) : null;
            return (now, history, coverage);
        }, cancellationToken);
        var (checks, storageHistory) = await DeskProbes.Run(DeskProbes.StateGate, Budget, ct =>
        {
            var snapshot = DashboardSnapshot.ReadChecks(paths, ct);
            return (snapshot, DashboardSnapshot.MergeStorageHistory(snapshot, DeskState.ReadDiagnostics(paths, ct)));
        }, cancellationToken, waitForSlot: true);
        var charts = new DashboardSnapshot.Charts(now, coverage, history, checks.Media,
            DashboardSnapshot.CapacityHistory(storageHistory));
        await DeskProbes.Run(DeskProbes.StateGate, Budget, ct =>
        {
            var previous = DashboardSnapshot.ReadCharts(paths, ct);
            return previous is not null && previous.SampledAt >= now || DashboardSnapshot.SaveCharts(paths, charts, ct);
        }, cancellationToken, waitForSlot: true);
        progress.Report(100);
    }
}
