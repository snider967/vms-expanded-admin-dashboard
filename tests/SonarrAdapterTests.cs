using System.Net;
using System.Net.Http.Headers;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal static class SonarrAdapterTests
{
    private const string Secret = "sonarr-fixture-secret";
    private static readonly IntegrationConnection Connection = new(new("https://fixture.example/prefix/"), Authentication.ApiKey, null, Secret);

    internal static async Task Run()
    {
        var source = new FixtureHandler();
        using var http = new HttpClient(source);
        var adapter = new SonarrAdapter(new IntegrationHttp(http));
        var healthy = await adapter.ObserveAsync(Connection, CancellationToken.None);
        Check(healthy.Compatibility == Compatibility.ApparentlyCompatible && healthy.Health == ServiceHealth.Healthy
            && healthy.ApplicationVersion == "99.99.99-fixture" && healthy.ApiVersion == "v3", "Compatible new version rejected.");
        Check(Metric(healthy, "series", "monitoredSeries") == 1
            && Metric(healthy, "missingEpisodes", "missingMonitoredEpisodes") == 237
            && Metric(healthy, "queue", "queueSampleWarnings") == 1
            && Metric(healthy, "queue", "queueSampleErrors") == 1
            && Metric(healthy, "queue", "queueSampleImportWarnings") == 1, "Incorrect normalized metrics.");
        Check(source.Requests.Count == 5 && source.Requests.Any(uri => uri.Query.Contains("monitored=true"))
            && source.Requests.All(uri => uri.AbsolutePath.StartsWith("/prefix/api/v3/", StringComparison.Ordinal)),
            "Unexpected service request count, filtering or prefix behavior.");

        source.SeriesOverride = """[{"monitored":"changed"}]""";
        source.QueueOverride = """{"totalRecords":1,"records":[{"trackedDownloadStatus":"future","trackedDownloadState":null}]}""";
        var partial = await adapter.ObserveAsync(Connection, CancellationToken.None);
        Check(partial.Compatibility == Compatibility.Partial
            && partial.Capabilities.Single(cap => cap.Key == "series").Status == CapabilityStatus.Incompatible
            && Metric(partial, "queue", "queueTotal") == 1 && Metric(partial, "queue", "queueSampleErrors") is null
            && Metric(partial, "missingEpisodes", "missingMonitoredEpisodes") == 237, "Breaking fields affected unrelated capabilities.");

        source.StatusCode = HttpStatusCode.Unauthorized;
        var before = source.Requests.Count;
        var denied = await adapter.ObserveAsync(Connection, CancellationToken.None);
        Check(denied.Access == AccessStatus.AuthenticationFailed && source.Requests.Count == before + 1,
            "Authentication failure should stop the probe.");
        source.StatusCode = HttpStatusCode.Redirect;
        before = source.Requests.Count;
        var redirect = await adapter.ObserveAsync(Connection, CancellationToken.None);
        Check(redirect.DiagnosticCode == "redirect-refused" && source.Requests.Count == before + 1,
            "Redirect should not lead to another request.");
        source.StatusCode = HttpStatusCode.OK;
        source.RateLimitHealth = true;
        before = source.Requests.Count;
        var rateLimited = await adapter.ObserveAsync(Connection, CancellationToken.None);
        Check(rateLimited.RetryAfter == TimeSpan.FromSeconds(45) && source.Requests.Count == before + 2,
            "Rate limiting must stop remaining reads and retain Retry-After.");
        source.RateLimitHealth = false;
        source.StatusCode = HttpStatusCode.NotFound;
        source.AllMissing = true;
        var unsupported = await adapter.ObserveAsync(Connection, CancellationToken.None);
        Check(unsupported.Compatibility == Compatibility.Unsupported && unsupported.Connectivity == Connectivity.Reachable,
            "An unsupported API should remain reachable.");
        source.AllMissing = false;
        source.StatusCode = HttpStatusCode.OK;
        source.MalformedStatus = true;
        var malformed = await adapter.ObserveAsync(Connection, CancellationToken.None);
        Check(malformed.Compatibility == Compatibility.Partial && malformed.DiagnosticCode == "invalid-response"
            && Metric(malformed, "missingEpisodes", "missingMonitoredEpisodes") == 237, "Malformed version response disabled valid reads.");
        source.MalformedStatus = false;
        source.StatusDelay = true;
        var fastTimeout = new IntegrationConnection(Connection.BaseUri, Authentication.ApiKey, null, Secret, timeoutSeconds: 1);
        var timeout = await adapter.ObserveAsync(fastTimeout, CancellationToken.None);
        Check(timeout.Connectivity == Connectivity.TimedOut && timeout.DiagnosticCode == "request-timeout", "Timeout classification is incorrect.");
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(15)))
        {
            try
            {
                await adapter.ObserveAsync(Connection, cancellation.Token);
                throw new InvalidOperationException("The read ignored cancellation.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        foreach (var address in new[] { "127.0.0.1", "::1", "10.0.0.1", "192.168.0.1", "fd00::10", "1.1.1.1" })
            Check(IntegrationHttp.AddressAllowed(IPAddress.Parse(address)), "A real local or remote destination was denied.");
        foreach (var address in new[] { "169.254.169.254", "100.100.100.200", "168.63.129.16", "0.0.0.0",
                     "::ffff:169.254.169.254", "fd00:ec2::254", "fe80::1" })
            Check(!IntegrationHttp.AddressAllowed(IPAddress.Parse(address)), "A restricted destination was accepted.");

        Check(IntegrationHttp.PrivateAddress(IPAddress.Parse("127.0.0.1"))
            && IntegrationHttp.PrivateAddress(IPAddress.Parse("fd00::10"))
            && !IntegrationHttp.PrivateAddress(IPAddress.Parse("1.1.1.1")), "HTTP transport trust boundary is incorrect.");

        Console.WriteLine("Sonarr read-only endpoints, bounded request handling and forward-compatible capability probes passed against mock responses.");
    }

    private static double? Metric(IntegrationObservation result, string capability, string metric) =>
        result.Capabilities.Single(cap => cap.Key == capability).Metrics.SingleOrDefault(row => row.Name == metric)?.Value;

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        internal HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        internal bool RateLimitHealth { get; set; }
        internal bool AllMissing { get; set; }
        internal bool MalformedStatus { get; set; }
        internal bool StatusDelay { get; set; }
        internal string? SeriesOverride { get; set; }
        internal string? QueueOverride { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri?.Host == "fixture.example"
                && request.Headers.GetValues("X-Api-Key").Single() == Secret, "Unexpected method, origin or authentication.");
            var url = request.RequestUri!;
            Requests.Add(url);
            var endpoint = url.AbsolutePath["/prefix/api/v3/".Length..];
            if (AllMissing) return new(HttpStatusCode.NotFound);
            if (endpoint == "system/status" && StatusDelay) await Task.Delay(Timeout.Infinite, token);
            if (endpoint == "system/status" && StatusCode != HttpStatusCode.OK)
                return new(StatusCode) { Headers = { Location = new Uri("https://different.example/") } };
            if (endpoint == "health" && RateLimitHealth)
                return new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45)) } };
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(endpoint switch
                {
                    "system/status" => MalformedStatus ? "not JSON" : """{"version":"99.99.99-fixture","newField":true}""",
                    "health" => "[]",
                    "series" => SeriesOverride ?? """[{"monitored":true,"optional":null},{"monitored":false}]""",
                    "wanted/missing" => """{"totalRecords":237}""",
                    "queue" => QueueOverride ?? """{"totalRecords":2,"records":[{"trackedDownloadStatus":"warning","trackedDownloadState":"importPending"},{"trackedDownloadStatus":"error","trackedDownloadState":"failed"}]}""",
                    _ => throw new InvalidOperationException("Unexpected Sonarr endpoint.")
                })
            };
        }
    }
}
