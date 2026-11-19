using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.RemoteAdminDesk;

internal static class ArrHistory
{
    private const int JellyfinLimit = 120;
    private static readonly string HistoryRoute =
        $"history?page=1&pageSize={ArrQueue.PageSize}&sortKey=date&sortDirection=descending&eventType=1&eventType=3&eventType=4";
    private static readonly SemaphoreSlim LibraryGate = new(1, 1);

    private sealed record ArrEvent(string Title, string Event, DateTimeOffset? Date, string? ImportedPath);
    internal sealed record Entry(string Title, string Event, DateTimeOffset? Date, string? Jellyfin);
    internal sealed record History(string Service, string State, int? Total, Entry[] Items);
    internal sealed record Result(DateTimeOffset SampledAt, int JellyfinLimit, History[] Services);

    internal static async Task<Result> Read(DeskSettings.Values saved, ILibraryManager library, CancellationToken ct)
    {
        // DownloadIgnored has different filter values in the two services.
        var histories = await ArrQueue.ReadPages(saved, HistoryRoute + "&eventType=7", HistoryRoute + "&eventType=9",
            ReadEvent, ct);
        HashSet<string>? paths = null;
        if (histories.Any(history => history.Items.Any(item => item.ImportedPath is not null)))
        {
            try
            {
                paths = await DeskProbes.Run(LibraryGate, TimeSpan.FromSeconds(3), token => ReadIndexedPaths(library, token), ct);
            }
            catch (DeskProbeBusyException) { /* The Arr sample is still usable. */ }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }

        return new Result(DateTimeOffset.UtcNow, JellyfinLimit, histories.Select(history =>
            new History(history.Service, history.State, history.Total, history.Items.Select(item =>
                new Entry(item.Title, item.Event, item.Date, Match(item, paths))).ToArray())).ToArray());
    }

    private static HashSet<string> ReadIndexedPaths(ILibraryManager library, CancellationToken ct)
    {
        var items = library.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsVirtualItem = false,
            EnableTotalRecordCount = false,
            Limit = JellyfinLimit,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)]
        });
        ct.ThrowIfCancellationRequested();
        return items.Take(JellyfinLimit).Select(item => item.Path).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    private static string? Match(ArrEvent item, HashSet<string>? paths)
    {
        if (item.Event != "imported") return null;
        if (item.ImportedPath is null) return "no-path";
        if (paths is null) return "unavailable";
        return paths.Contains(item.ImportedPath) ? "indexed" : "not-in-sample";
    }

    private static ArrEvent ReadEvent(string service, JsonElement item)
    {
        var kind = item.TryGetProperty("eventType", out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.ToLowerInvariant(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        } : null;
        var label = kind switch
        {
            "grabbed" or "1" => "grabbed",
            "downloadfolderimported" or "3" => "imported",
            "downloadfailed" or "4" => "failed",
            "downloadignored" => "ignored",
            "7" when service == "Sonarr" => "ignored",
            "9" when service == "Radarr" => "ignored",
            _ => "other"
        };
        var date = item.TryGetProperty("date", out var timestamp) && timestamp.ValueKind == JsonValueKind.String
            && timestamp.TryGetDateTimeOffset(out var parsed) ? parsed : (DateTimeOffset?)null;
        var sourceTitle = ArrQueue.Text(item, "sourceTitle", 150) ?? "Untitled history entry";
        var title = string.Concat(sourceTitle.Where(character => !char.IsControl(character)));
        var path = label == "imported" && item.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? ReadImportedPath(data) : null;
        return new ArrEvent(title.Length == 0 ? "Untitled history entry" : title, label, date, path);
    }

    private static string? ReadImportedPath(JsonElement data)
    {
        foreach (var property in data.EnumerateObject())
        {
            if (property.Name.Equals("importedPath", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                var path = property.Value.GetString();
                return path is { Length: > 0 and <= 4096 } && Path.IsPathFullyQualified(path) ? path : null;
            }
        }
        return null;
    }
}
