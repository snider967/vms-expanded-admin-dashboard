using System.Reflection;
using System.Text.Json;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.RemoteAdminDesk;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Querying;

var directory = Directory.CreateTempSubdirectory("vms-trends-");
try
{
    var paths = Stub<IApplicationPaths>(method => method.Name == "get_DataPath"
        ? directory.FullName : throw new NotSupportedException(method.Name));
    var statePath = Directory.CreateDirectory(Path.Combine(directory.FullName, "remote-admin-desk")).FullName;
    var chartsPath = Path.Combine(statePath, "dashboard-charts.json");
    var mediaStatePath = Path.Combine(statePath, "media-baseline.json");
    var oldChecksPath = Path.Combine(statePath, "dashboard-checks.json");
    File.WriteAllText(mediaStatePath, """{"known":[],"pending":[],"lastScanAt":"2026-11-20T10:00:00Z"}""");
    File.WriteAllText(oldChecksPath, """{"media":{"sampledAt":"2026-11-20T10:00:00Z","coverageStart":"2026-11-20T09:00:00Z","days":[],"hours":[]},"storageHistory":[]}""");
    File.WriteAllText(chartsPath, """{"sampledAt":"2020-01-01T10:00:00Z","activityCoverageStart":"2020-01-01T09:00:00Z","activity":[],"capacity":[]}""");
    var originalState = File.ReadAllText(mediaStatePath);
    var originalChecks = File.ReadAllText(oldChecksPath);

    var now = DateTime.UtcNow;
    var recent = now.AddMinutes(-10);
    var older = new[] { now.Date.AddDays(-6), now.Date.AddDays(-29), now.Date.AddDays(-89) };
    var excluded = new[] { older[^1].AddTicks(-1), now.AddHours(1) };
    var starts = Enumerable.Repeat(recent, 501).Concat(older).Concat(excluded).ToArray();
    DateTime[] signIns = [recent, .. older];
    BaseItem[] items = [
        .. Enumerable.Range(0, 501).Select(_ => new Episode { DateCreated = recent }),
        .. Enumerable.Range(0, 121).Select(_ => new Movie { DateCreated = recent }),
        .. older.Concat(excluded).Select(date => new Movie { DateCreated = date })
    ];
    var incomplete = false;
    var failMedia = false;
    var empty = false;
    var activity = CreateStub<IActivityManager>((method, args) =>
    {
        Check(method.Name == "GetPagedResultAsync", "Unexpected activity call.");
        var query = (ActivityLogQuery)args![0]!;
        Check(query.HasUserId is true && query.Limit == int.MaxValue && query.MinDate is not null && query.MaxDate is not null,
            "Activity query must fetch a complete, dated window for accounts.");
        Check(query.MinDate == older[^1].AddTicks(-1), "Activity query starts at the wrong UTC boundary.");
        var dates = query.Type switch
        {
            "VideoPlayback" => starts,
            "AuthenticationSucceeded" => signIns,
            "AudioPlayback" or "GamePlayback" => [],
            _ => throw new InvalidOperationException("Unexpected activity type.")
        };
        var entries = empty ? [] : dates.Where(date => date >= query.MinDate).Select(date =>
            new ActivityLogEntry("Fixture", query.Type!, Guid.NewGuid()) { Date = date }).ToArray();
        return Task.FromResult(new QueryResult<ActivityLogEntry> { Items = entries, TotalRecordCount = entries.Length + (incomplete ? 1 : 0) });
    });
    var library = CreateStub<ILibraryManager>((method, args) =>
    {
        Check(method.Name == "GetItemList", "Unexpected library call.");
        var query = (InternalItemsQuery)args![0]!;
        Check(query.MinDateCreated == older[^1].AddTicks(-1) && query.Limit is null
            && query.Recursive && query.IsVirtualItem is false && query.IncludeItemTypes.Length == 2,
            "The library query must use the complete, dated window for indexed movies and episodes.");
        if (failMedia) throw new IOException("Simulated unavailable library.");
        return empty ? Array.Empty<BaseItem>() : items;
    });

    async Task Refresh() => await new UpdateVisualDashboard(activity, library, paths)
        .ExecuteAsync(new Progress<double>(), CancellationToken.None);

    void CheckSavedCounts()
    {
        using var snapshot = JsonDocument.Parse(File.ReadAllText(chartsPath));
        var charts = snapshot.RootElement;
        Check(charts.GetProperty("activityCoverageStart").ValueKind == JsonValueKind.Null, "Complete activity marked partial.");
        var media = charts.GetProperty("media");
        Check(media.GetProperty("coverageStart").ValueKind == JsonValueKind.Null, "Complete media marked partial.");
        foreach (var (days, extra) in new[] { (7, 1), (30, 2), (90, 3) })
        {
            var cutoff = DateOnly.FromDateTime(now.Date.AddDays(1 - days));
            Check(Sum(charts, "activity", "starts", cutoff) == 501 + extra, $"Incorrect {days}d playback count.");
            Check(Sum(charts, "activity", "signIns", cutoff) == 1 + extra, $"Incorrect {days}d sign-in count.");
            Check(Sum(media, "days", "movies", cutoff) == 121 + extra, $"Incorrect {days}d movie count.");
            Check(Sum(media, "days", "episodes", cutoff) == 501, $"Incorrect {days}d episode count.");
        }
        Check(Sum(charts, "activityHours", "starts") == 501 && Sum(charts, "activityHours", "signIns") == 1,
            "Incorrect rolling 24h activity count.");
        Check(Sum(media, "hours", "movies") == 121 && Sum(media, "hours", "episodes") == 501,
            "Incorrect rolling 24h indexed count.");
    }

    await Refresh();
    CheckSavedCounts();
    await Refresh();
    CheckSavedCounts(); // A new task instance rereads the same persisted state.
    Check(File.ReadAllText(mediaStatePath) == originalState && File.ReadAllText(oldChecksPath) == originalChecks,
        "The chart refresh modified existing notification checkpoints or visual checks.");

    var completeSnapshot = File.ReadAllText(chartsPath);
    incomplete = true;
    await ExpectFailure(Refresh);
    Check(File.ReadAllText(chartsPath) == completeSnapshot, "Incomplete activity replaced the last good snapshot.");
    incomplete = false;
    failMedia = true;
    await ExpectFailure(Refresh);
    Check(File.ReadAllText(chartsPath) == completeSnapshot, "Unavailable library replaced the last good snapshot.");
    failMedia = false;
    empty = true;
    await Refresh();
    using var emptySnapshot = JsonDocument.Parse(File.ReadAllText(chartsPath));
    Check(emptySnapshot.RootElement.GetProperty("activity").GetArrayLength() == 0 &&
        emptySnapshot.RootElement.GetProperty("media").GetProperty("days").GetArrayLength() == 0,
        "A successfully collected empty window should have no events.");
    Console.WriteLine("Scheduled Trends collection, UTC ranges, repeat refresh and persisted state passed.");
}
finally
{
    directory.Delete(recursive: true);
}

static int Sum(JsonElement root, string series, string key, DateOnly? cutoff = null) =>
    root.GetProperty(series).EnumerateArray().Where(row =>
        cutoff is null || DateOnly.Parse(row.GetProperty("date").GetString()!) >= cutoff)
        .Sum(row => row.GetProperty(key).GetInt32());

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task ExpectFailure(Func<Task> operation)
{
    try { await operation(); }
    catch (IOException) { return; }
    throw new InvalidOperationException("The refresh unexpectedly succeeded.");
}

static T Stub<T>(Func<MethodInfo, object?> operation) where T : class => CreateStub<T>((method, _) => operation(method));

static T CreateStub<T>(Func<MethodInfo, object?[]?, object?> operation) where T : class
{
    var proxy = DispatchProxy.Create<T, TestProxy>();
    ((TestProxy)(object)proxy).Operation = operation;
    return proxy;
}

public class TestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Operation { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Operation(targetMethod!, args);
}
