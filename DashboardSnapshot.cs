using Jellyfin.Data.Enums;
using Jellyfin.Data.Queries;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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

    private static int HourIndex(DateTimeOffset value, DateTimeOffset sampledAt) =>
        Math.Min(23, (int)Math.Floor((value - sampledAt.AddHours(-24)).TotalHours));

    internal static DateTime WindowStart(DateTimeOffset sampledAt) =>
        sampledAt.UtcDateTime.Date.AddDays(1 - HistoryDays);

    // Daily totals are aligned to UTC dates. The hourly view uses a rolling 24 hours.
    private static void Count(DateTime date, DateTimeOffset sampledAt, int[] days, int[] hours, DateTime start)
    {
        date = date.ToUniversalTime();
        if (date < start || date > sampledAt.UtcDateTime) return;

        days[(date.Date - start).Days]++;
        var hour = HourIndex(date, sampledAt);
        if (hour >= 0) hours[hour]++;
    }

    internal static async Task<(ActivityDay[] Days, ActivityHour[] Hours)> CollectActivity(
        IActivityManager activityManager, DateTimeOffset sampledAt, CancellationToken ct)
    {
        var start = WindowStart(sampledAt);
        var starts = new int[HistoryDays];
        var signIns = new int[HistoryDays];
        var hourlyStarts = new int[24];
        var hourlySignIns = new int[24];
        foreach (var (type, days, hours) in new[]
        {
            ("VideoPlayback", starts, hourlyStarts),
            ("AudioPlayback", starts, hourlyStarts),
            ("GamePlayback", starts, hourlyStarts),
            ("AuthenticationSucceeded", signIns, hourlySignIns)
        })
        {
            ct.ThrowIfCancellationRequested();
            var result = await activityManager.GetPagedResultAsync(new ActivityLogQuery
            {
                HasUserId = true,
                MinDate = start.AddTicks(-1),
                MaxDate = sampledAt.UtcDateTime.AddTicks(1),
                Type = type,
                // A missing limit defaults to 100 in Jellyfin 12.4.
                Limit = int.MaxValue
            });
            ct.ThrowIfCancellationRequested();
            if (result.TotalRecordCount > result.Items.Count)
                throw new IOException("Jellyfin returned an incomplete dashboard activity query.");

            foreach (var entry in result.Items)
                Count(entry.Date, sampledAt, days, hours, start);
        }

        return (
            Enumerable.Range(0, HistoryDays).Where(i => starts[i] > 0 || signIns[i] > 0)
                .Select(i => new ActivityDay(DateOnly.FromDateTime(start.AddDays(i)), starts[i], signIns[i])).ToArray(),
            Enumerable.Range(0, 24).Where(i => hourlyStarts[i] > 0 || hourlySignIns[i] > 0)
                .Select(i => new ActivityHour(i, hourlyStarts[i], hourlySignIns[i])).ToArray());
    }

    internal static MediaHistory CollectMedia(ILibraryManager library, DateTimeOffset sampledAt, CancellationToken ct)
    {
        var start = WindowStart(sampledAt);
        var movies = new int[HistoryDays];
        var episodes = new int[HistoryDays];
        var hourlyMovies = new int[24];
        var hourlyEpisodes = new int[24];
        ct.ThrowIfCancellationRequested();
        var items = library.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsVirtualItem = false,
            EnableTotalRecordCount = false,
            MinDateCreated = start.AddTicks(-1)
        });
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item is Episode)
                Count(item.DateCreated, sampledAt, episodes, hourlyEpisodes, start);
            else
                Count(item.DateCreated, sampledAt, movies, hourlyMovies, start);
        }

        return new MediaHistory(sampledAt, null,
            Enumerable.Range(0, HistoryDays).Where(i => movies[i] > 0 || episodes[i] > 0)
                .Select(i => new MediaDay(DateOnly.FromDateTime(start.AddDays(i)), movies[i], episodes[i])).ToArray(),
            Enumerable.Range(0, 24).Where(i => hourlyMovies[i] > 0 || hourlyEpisodes[i] > 0)
                .Select(i => new MediaHour(i, hourlyMovies[i], hourlyEpisodes[i])).ToArray());
    }

    internal static Checks ReadChecks(IApplicationPaths paths, CancellationToken ct) =>
        DeskState.Read<Checks>(paths, ChecksFile, ct) ?? new Checks();

    internal static Charts? ReadCharts(IApplicationPaths paths, CancellationToken ct) =>
        DeskState.Read<Charts>(paths, ChartsFile, ct);

    // The caller holds StateGate across each read, merge and write.
    internal static bool UpdateChecks(IApplicationPaths paths, CancellationToken ct,
        DeskProbes.StorageReport? storage = null, TimedBranches? branches = null)
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

public sealed class UpdateVisualDashboard(
    IActivityManager activityManager, ILibraryManager library, IApplicationPaths paths) : IScheduledTask
{
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly TimeSpan CollectionBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StateBudget = TimeSpan.FromSeconds(3);

    public string Name => "Update visual dash";
    public string Key => "RemoteAdminDeskVisual";
    public string Description => "Refresh the Overview charts from recorded account activity, library additions and saved storage samples.";
    public string Category => Plugin.DisplayName;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await DeskProbes.RunAsync(RefreshGate, CollectionBudget, async ct =>
        {
            var now = DateTimeOffset.UtcNow;
            var (history, hours) = await DashboardSnapshot.CollectActivity(activityManager, now, ct);
            var media = DashboardSnapshot.CollectMedia(library, now, ct);
            return await DeskProbes.Run(DeskProbes.StateGate, StateBudget, token =>
            {
                var previous = DashboardSnapshot.ReadCharts(paths, token);
                if (previous is not null && previous.SampledAt >= now) return true;
                var checks = DashboardSnapshot.ReadChecks(paths, token);
                var storageHistory = DashboardSnapshot.MergeStorageHistory(checks, DeskState.ReadDiagnostics(paths, token));
                var charts = new DashboardSnapshot.Charts(now, null, history, media,
                    DashboardSnapshot.CapacityHistory(storageHistory, now), hours);
                return DashboardSnapshot.SaveCharts(paths, charts, token);
            }, ct, waitForSlot: true);
        }, cancellationToken);
        progress.Report(100);
    }
}
