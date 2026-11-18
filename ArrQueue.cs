using System.Net;
using System.Text.Json;

namespace Jellyfin.Plugin.RemoteAdminDesk;

internal static class ArrQueue
{
    private const int MaxBytes = 256 * 1024;
    private const int PageSize = 20;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2),
        MaxConnectionsPerServer = 1, PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

    internal sealed record Item(string Title, string? Status, string? State, string[] Reasons);
    internal sealed record Queue(string Service, string State, int? Total, Item[] Items);

    internal static async Task<Queue[]> Read(DeskSettings.Values saved, CancellationToken cancellationToken)
    {
        if (!Gate.Wait(0)) throw new DeskProbeBusyException();
        try
        {
            return await Task.WhenAll(
                ReadService("Sonarr", 8989, DeskSettings.Effective("RAD_SONARR_API_KEY", saved.SonarrKey), cancellationToken),
                ReadService("Radarr", 7878, DeskSettings.Effective("RAD_RADARR_API_KEY", saved.RadarrKey), cancellationToken));
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static bool IsApiKey(string key) => key.Length is >= 1 and <= 128 && key.All(char.IsAsciiLetterOrDigit);

    private static async Task<Queue> ReadService(string service, int port, string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key)) return new Queue(service, "missing", null, []);
        if (!IsApiKey(key))
            return new Queue(service, "invalid", null, []);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        var ct = budget.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://127.0.0.1:{port}/api/v3/queue?page=1&pageSize={PageSize}&includeUnknownSeriesItems=true&includeUnknownMovieItems=true");
        request.Headers.Add("X-Api-Key", key);
        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new Queue(service, "unauthorized", null, []);
            if (!response.IsSuccessStatusCode) return new Queue(service, "api-error", null, []);
            if (response.Content.Headers.ContentLength > MaxBytes)
                return new Queue(service, "response-limit", null, []);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var bytes = new byte[MaxBytes + 1];
            var received = await body.ReadAtLeastAsync(bytes, bytes.Length, false, ct);
            if (received == bytes.Length) return new Queue(service, "response-limit", null, []);
            using var doc = JsonDocument.Parse(bytes.AsMemory(0, received), new JsonDocumentOptions { MaxDepth = 24 });
            var result = doc.RootElement;
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                return new Queue(service, "invalid-response", null, []);
            var total = result.TryGetProperty("totalRecords", out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) ? count : (int?)null;
            return new Queue(service, "ready", total, records.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object).Take(PageSize).Select(ReadItem).ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Queue(service, "timeout", null, []);
        }
        catch (HttpRequestException)
        {
            return new Queue(service, "unreachable", null, []);
        }
        catch (JsonException)
        {
            return new Queue(service, "invalid-response", null, []);
        }
        catch (IOException)
        {
            return new Queue(service, "unreachable", null, []);
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

    private static string? Text(JsonElement value, string key, int limit) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? Clip(field.GetString(), limit) : null;
    private static string? Clip(string? value, int limit) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= limit ? value : value[..limit];
}
