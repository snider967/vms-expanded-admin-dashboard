using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal static class ProwlarrAdapterTests
{
    internal static async Task Run()
    {
        using var fixture = new Fixtures();
        using var http = new HttpClient(fixture);
        var adapter = new ProwlarrAdapter(new(http), new FixtureClock());
        var connection = new IntegrationConnection(new("https://index.example/prowlarr/"), Authentication.ApiKey, null, "fixture-key");
        var result = await adapter.ObserveAsync(connection, CancellationToken.None);
        Check(result.ApplicationVersion == "2.3.1" && result.ApiVersion == "v1" && result.Compatibility == Compatibility.ApparentlyCompatible,
            "Prowlarr identity and version mismatch.");
        Check(Metric(result, "indexers", "enabledIndexers") == 1
            && Metric(result, "indexerStatus", "indexersInBackoff") == 1
            && Metric(result, "statistics", "indexerQueries24h") == 21
            && Metric(result, "statistics", "slowestIndexerMeanResponseMs24h") == 275
            && Metric(result, "applications", "applicationsWithSyncConfigured") == 1,
            "Prowlarr capabilities do not match the source statistics.");
        Check(!JsonSerializer.Serialize(result).Contains("sensitive-fixture-value"), "Prowlarr leaked upstream fields.");

        fixture.Partial = true;
        var partial = await adapter.ObserveAsync(connection, CancellationToken.None);
        Check(partial.Compatibility == Compatibility.Partial
            && partial.Capabilities.Single(cap => cap.Key == "indexerStatus").Status == CapabilityStatus.Unsupported
            && Metric(partial, "indexers", "enabledIndexers") == 1
            && Metric(partial, "applications", "configuredApplications") == 1
            && Metric(partial, "applications", "applicationsWithSyncConfigured") is null,
            "Unsupported and changed Prowlarr capabilities were not scoped correctly.");
        Console.WriteLine("Prowlarr mock health, indexers, bounded statistics, application configuration and partial compatibility passed.");
    }

    private static double? Metric(IntegrationObservation result, string capability, string metric) =>
        result.Capabilities.Single(row => row.Key == capability).Metrics.SingleOrDefault(row => row.Name == metric)?.Value;

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Fixtures : HttpMessageHandler
    {
        internal bool Partial { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Check(request.Method == HttpMethod.Get && uri.Host == "index.example"
                && uri.AbsolutePath.StartsWith("/prowlarr/api/v1/", StringComparison.Ordinal)
                && request.Headers.GetValues("X-Api-Key").Single() == "fixture-key", "Prowlarr read escaped its configured endpoint.");
            var route = uri.AbsolutePath["/prowlarr/api/v1/".Length..];
            if (route == "indexerstatus" && Partial) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (route == "indexerstats")
                Check(Uri.UnescapeDataString(uri.Query) == "?startDate=2026-11-23T12:00:00.0000000Z&endDate=2026-11-24T12:00:00.0000000Z",
                    "Prowlarr statistics must limit the time window.");
            var body = route switch
            {
                "system/status" => """{"version":"2.3.1"}""",
                "health" => "[]",
                "indexer" => """[{"enable":true,"fields":["sensitive-fixture-value"]},{"enable":false}]""",
                "indexerstatus" => """[{"mostRecentFailure":"2026-11-24T11:58:00Z","disabledTill":"2026-11-24T12:15:00Z"},{"mostRecentFailure":null,"disabledTill":null}]""",
                "indexerstats" => """{"indexers":[{"numberOfQueries":9,"numberOfFailedQueries":1,"averageResponseTime":85},{"numberOfQueries":12,"numberOfFailedQueries":3,"averageResponseTime":275}]}""",
                "applications" => Partial ? """[{"enable":true,"syncLevel":"future"}]""" : """[{"enable":true,"syncLevel":"fullSync"}]""",
                _ => throw new InvalidOperationException("Unexpected Prowlarr route.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
