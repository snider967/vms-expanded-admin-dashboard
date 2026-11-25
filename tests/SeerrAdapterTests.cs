using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal static class SeerrAdapterTests
{
    internal static async Task Run()
    {
        using var fixture = new Fixture();
        using var client = new HttpClient(fixture);
        var adapter = new SeerrAdapter(new(client));
        var connection = new IntegrationConnection(new("https://requests.example/seerr/"), Authentication.ApiKey, null, "fixture-key");
        var result = await adapter.ObserveAsync(connection, CancellationToken.None);
        Check(result.ApplicationVersion == "3.0.0" && result.Access == AccessStatus.Authenticated
            && result.Compatibility == Compatibility.ApparentlyCompatible && result.Health == ServiceHealth.Unknown,
            "Public Seerr status must be independent of authentication and service health.");
        Check(result.Capabilities.SelectMany(cap => cap.Metrics).Any(metric => metric is { Name: "failedRequests", Value: 5 })
            && !JsonSerializer.Serialize(result).Contains("private-user"), "The failed count was incorrect or requester data escaped.");

        fixture.WrongFilter = true;
        result = await adapter.ObserveAsync(connection, CancellationToken.None);
        Check(result.Compatibility == Compatibility.Partial
            && result.Capabilities.Single(cap => cap.Key == "requestFailures").Status == CapabilityStatus.Incompatible,
            "An unrecognized failed filter was presented as a verified count.");

        fixture.Denied = true;
        result = await adapter.ObserveAsync(connection, CancellationToken.None);
        Check(result.Connectivity == Connectivity.Reachable && result.Access == AccessStatus.AuthenticationFailed
            && result.ApplicationVersion == "3.0.0", "Public Seerr status incorrectly authenticated the request API.");
        Console.WriteLine("Seerr mock public status, authenticated request counts, filter verification and requester redaction passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        internal bool WrongFilter { get; set; }
        internal bool Denied { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Check(uri.Host == "requests.example" && uri.AbsolutePath.StartsWith("/seerr/api/v1/", StringComparison.Ordinal)
                && request.Method == HttpMethod.Get, "Seerr request left its configured read-only scope.");
            var endpoint = uri.AbsolutePath["/seerr/api/v1/".Length..];
            Check(endpoint == "status" ? !request.Headers.Contains("X-Api-Key")
                : request.Headers.GetValues("X-Api-Key").Single() == "fixture-key", "Incorrect credential scope.");
            if (Denied && endpoint != "status") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var body = endpoint switch
            {
                "status" => """{"version":"3.0.0"}""",
                "request/count" => """{"total":10,"approved":3,"pending":2,"available":1}""",
                "request" when uri.Query == "?take=1&skip=0&filter=failed" => WrongFilter
                    ? """{"pageInfo":{"results":5},"results":[{"status":1}]}"""
                    : """{"pageInfo":{"results":5},"results":[{"status":4,"requestedBy":{"email":"private-user"}}]}""",
                _ => throw new InvalidOperationException("Unexpected Seerr request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
