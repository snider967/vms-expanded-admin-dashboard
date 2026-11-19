using System.Net;
using System.Text.Json;

namespace Jellyfin.Plugin.RemoteAdminDesk;

internal static class ArrQueue
{
    private const int MaxBytes = 256 * 1024;
    internal const int PageSize = 20;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2),
        MaxConnectionsPerServer = 1, PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

    internal sealed record Item(string Title, string? Status, string? State, string[] Reasons);
    internal sealed record Queue(string Service, string State, int? Total, Item[] Items);

    internal sealed record Page<T>(string Service, string State, int? Total, T[] Items);

    internal static async Task<Queue[]> Read(DeskSettings.Values saved, CancellationToken cancellationToken)
    {
        var route = $"queue?page=1&pageSize={PageSize}&includeUnknownSeriesItems=true&includeUnknownMovieItems=true";
        var pages = await ReadPages(saved, route, route, (_, item) => ReadItem(item), cancellationToken);
        return pages.Select(page => new Queue(page.Service, page.State, page.Total, page.Items)).ToArray();
    }

    internal static async Task<Page<T>[]> ReadPages<T>(DeskSettings.Values saved, string sonarrRoute, string radarrRoute,
        Func<string, JsonElement, T> readItem, CancellationToken cancellationToken)
    {
        if (!Gate.Wait(0)) throw new DeskProbeBusyException();
        try
        {
            return await Task.WhenAll(
                ReadService("Sonarr", 8989, DeskSettings.Effective("RAD_SONARR_API_KEY", saved.SonarrKey),
                    sonarrRoute, readItem, cancellationToken),
                ReadService("Radarr", 7878, DeskSettings.Effective("RAD_RADARR_API_KEY", saved.RadarrKey),
                    radarrRoute, readItem, cancellationToken));
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static bool IsApiKey(string key) => key.Length is >= 1 and <= 128 && key.All(char.IsAsciiLetterOrDigit);

    private static async Task<Page<T>> ReadService<T>(string service, int port, string? key, string route,
        Func<string, JsonElement, T> readItem, CancellationToken cancellationToken)
    {
        Page<T> Failed(string state) => new(service, state, null, []);
        if (string.IsNullOrWhiteSpace(key)) return Failed("missing");
        if (!IsApiKey(key)) return Failed("invalid");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        var ct = budget.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://127.0.0.1:{port}/api/v3/{route}");
        request.Headers.Add("X-Api-Key", key);
        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Failed("unauthorized");
            if (!response.IsSuccessStatusCode) return Failed("api-error");
            if (response.Content.Headers.ContentLength > MaxBytes)
                return Failed("response-limit");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var bytes = new byte[MaxBytes + 1];
            var received = await body.ReadAtLeastAsync(bytes, bytes.Length, false, ct);
            if (received == bytes.Length) return Failed("response-limit");
            using var doc = JsonDocument.Parse(bytes.AsMemory(0, received), new JsonDocumentOptions { MaxDepth = 24 });
            var result = doc.RootElement;
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                return Failed("invalid-response");
            var total = result.TryGetProperty("totalRecords", out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) ? count : (int?)null;
            return new Page<T>(service, "ready", total, records.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object).Take(PageSize).Select(item => readItem(service, item)).ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("timeout");
        }
        catch (HttpRequestException)
        {
            return Failed("unreachable");
        }
        catch (JsonException)
        {
            return Failed("invalid-response");
        }
        catch (IOException)
        {
            return Failed("unreachable");
        }
    }

    private static Item ReadItem(JsonElement item)
    {
        var reasons = new List<string>(3);
        if (Text(item, "errorMessage", 180) is { } error) reasons.Add(error);
        if (item.TryGetProperty("statusMessages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray().Take(3))
            {
                if (message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("messages", out var lines) || lines.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var line in lines.EnumerateArray().Take(3 - reasons.Count))
                {
                    if (line.ValueKind == JsonValueKind.String && Clip(line.GetString(), 180) is { } reason)
                        reasons.Add(reason);
                }
                if (reasons.Count == 3) break;
            }
        }
        return new Item(Text(item, "title", 150) ?? "Untitled queue entry", Text(item, "trackedDownloadStatus", 50)
            ?? Text(item, "status", 50), Text(item, "trackedDownloadState", 50), reasons.ToArray());
    }

    internal static string? Text(JsonElement value, string key, int limit) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? Clip(field.GetString(), limit) : null;
    private static string? Clip(string? value, int limit) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= limit ? value : value[..limit];
}
