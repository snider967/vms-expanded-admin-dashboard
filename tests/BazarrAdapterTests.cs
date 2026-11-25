using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal static class BazarrAdapterTests
{
    internal static async Task Run()
    {
        using var fixture = new Fixtures();
        using var http = new HttpClient(fixture);
        var adapter = new BazarrAdapter(new(http));
        var connection = new IntegrationConnection(new("https://subs.example/subtitles/"), Authentication.ApiKey, null, "mock-key");
        var result = await adapter.ObserveAsync(connection, CancellationToken.None);
        Check(result.ApplicationVersion == "v1.6.2" && result.Compatibility == Compatibility.ApparentlyCompatible
            && result.Health == ServiceHealth.Degraded, "Bazarr identity or health mismatch.");
        Check(Metric(result, "subtitles", "wantedEpisodeSubtitles") == 120
            && Metric(result, "subtitles", "wantedMovieSubtitles") == 15
            && Metric(result, "providers", "reportedProviderIssues") == 1
            && Metric(result, "activity", "episodeSubtitleHistoryEventsWeek") == 5, "Bazarr monitoring totals are incorrect.");
        Check(!JsonSerializer.Serialize(result).Contains("raw-private-detail"), "Raw provider or health details escaped the adapter.");

        fixture.Partial = true;
        var partial = await adapter.ObserveAsync(connection, CancellationToken.None);
        Check(partial.Compatibility == Compatibility.Partial && partial.Health == ServiceHealth.Degraded
            && Metric(partial, "subtitles", "wantedEpisodeSubtitles") == 120 && Metric(partial, "subtitles", "wantedMovieSubtitles") is null
            && partial.Capabilities.Single(row => row.Key == "providers").Status == CapabilityStatus.Unsupported
            && Metric(partial, "activity", "movieSubtitleHistoryEventsWeek") == 1,
            "Missing subtitle data or provider support degraded unrelated capability results.");
        Console.WriteLine("Bazarr mock health, wanted counts, provider status, history and redaction passed.");
    }

    private static double? Metric(IntegrationObservation result, string capability, string metric) =>
        result.Capabilities.Single(cap => cap.Key == capability).Metrics.SingleOrDefault(row => row.Name == metric)?.Value;

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private sealed class Fixtures : HttpMessageHandler
    {
        internal bool Partial { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Check(request.Method == HttpMethod.Get && uri.Host == "subs.example"
                && uri.AbsolutePath.StartsWith("/subtitles/api/", StringComparison.Ordinal)
                && request.Headers.GetValues("X-Api-Key").Single() == "mock-key", "Bazarr did not use the configured read-only origin and prefix.");
            var route = uri.AbsolutePath["/subtitles/api/".Length..];
            if (route == "providers" && Partial) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (route == "history/stats") Check(uri.Query == "?timeFrame=week", "History must use a bounded time frame.");
            var payload = route switch
            {
                "system/status" => """{"data":{"bazarr_version":"v1.6.2","bazarr_config_directory":"raw-private-detail"}}""",
                "system/health" => """{"data":[{"message":"raw-private-detail"}]}""",
                "badges" => Partial ? """{"episodes":120,"movies":null}""" : """{"episodes":120,"movies":15,"extra":true}""",
                "providers" => """{"data":[{"status":"Good"},{"status":"raw-private-detail"}]}""",
                "history/stats" => """{"series":[{"count":2},{"count":3}],"movies":[{"count":1}]}""",
                _ => throw new InvalidOperationException("Unexpected Bazarr route.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
        }
    }
}
