using System.Net;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal static class RadarrAdapterTests
{
    internal static async Task Run()
    {
        using var fixture = new Fixtures();
        using var http = new HttpClient(fixture);
        var transport = new IntegrationHttp(http);
        var radarr = new RadarrAdapter(transport);
        var sonarr = new SonarrAdapter(transport);
        var movies = new IntegrationConnection(new("https://movies.example/radarr/"), Authentication.ApiKey, null, "mock-movies");
        var series = new IntegrationConnection(new("https://series.example/sonarr/"), Authentication.ApiKey, null, "mock-series");

        var baseline = await radarr.ObserveAsync(movies, CancellationToken.None);
        Check(baseline.Id == IntegrationId.Radarr && baseline.ApplicationVersion == "6.3.1"
            && baseline.Compatibility == Compatibility.ApparentlyCompatible
            && baseline.Health == ServiceHealth.Degraded && baseline.Access == AccessStatus.Authenticated,
            "Radarr metadata or health does not match the response.");
        Check(Metric(baseline, "movies", "monitoredMovies") == 2
            && Metric(baseline, "missingMovies", "missingMonitoredMovies") == 14
            && Metric(baseline, "queue", "queueTotal") == 1 && Metric(baseline, "queue", "queueSampleImportWarnings") == 1,
            "Radarr movies, missing count or queue metrics were parsed incorrectly.");

        fixture.Incompatible = true;
        var results = await Task.WhenAll(radarr.ObserveAsync(movies, CancellationToken.None),
            sonarr.ObserveAsync(series, CancellationToken.None));
        Check(results[0].Compatibility == Compatibility.Partial
            && results[0].Capabilities.Single(row => row.Key == "movies").Status == CapabilityStatus.Incompatible
            && Metric(results[0], "missingMovies", "missingMonitoredMovies") == 14, "Radarr lost unaffected capability coverage.");
        Check(results[1].Compatibility == Compatibility.ApparentlyCompatible
            && Metric(results[1], "series", "monitoredSeries") == 2, "A breaking Radarr response affected Sonarr.");
        Console.WriteLine("Radarr v3 mock capabilities, contract changes and Sonarr fault isolation passed.");
    }

    private static double? Metric(IntegrationObservation result, string capability, string metric) =>
        result.Capabilities.Single(row => row.Key == capability).Metrics.SingleOrDefault(row => row.Name == metric)?.Value;

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private sealed class Fixtures : HttpMessageHandler
    {
        internal bool Incompatible { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var isRadarr = uri.Host == "movies.example";
            var prefix = isRadarr ? "/radarr/api/v3/" : "/sonarr/api/v3/";
            Check(uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) && request.Method == HttpMethod.Get
                && request.Headers.GetValues("X-Api-Key").Single() == (isRadarr ? "mock-movies" : "mock-series"),
                "Adapter request escaped its configured prefix, origin or credential scope.");
            var route = uri.AbsolutePath[prefix.Length..];
            if (route == "wanted/missing") Check(uri.Query.Contains("monitored=true"), "Missing counts must filter monitored media.");
            if (route == "queue") Check(uri.Query.Contains(isRadarr ? "includeUnknownMovieItems=true" : "includeUnknownSeriesItems=true"),
                "Incorrect service queue query.");
            var body = route switch
            {
                "system/status" => isRadarr ? """{"version":"6.3.1"}""" : """{"version":"4.0.20"}""",
                "health" => """[{"type":"warning","message":"Sensitive upstream detail"}]""",
                "wanted/missing" => """{"totalRecords":14}""",
                "queue" => """{"totalRecords":1,"records":[{"trackedDownloadStatus":"warning","trackedDownloadState":"importBlocked"}]}""",
                "movie" when isRadarr && Incompatible => """{"schema":"breaking-change"}""",
                "movie" when isRadarr => """[{"monitored":true},{"monitored":true},{"monitored":false,"ignored":null}]""",
                "series" when !isRadarr => """[{"monitored":true},{"monitored":true}]""",
                _ => throw new InvalidOperationException("Unexpected endpoint.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
