using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal static class QBittorrentAdapterTests
{
    internal static async Task Run()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var adapter = new QBittorrentAdapter(new(http));
        var uri = new Uri("https://downloads.example/qbt/");
        var result = await adapter.ObserveAsync(new(uri, Authentication.Session, "mock-user", "mock-password"), CancellationToken.None);
        Check(result.ApplicationVersion == "v5.1.0" && result.ApiVersion == "2.11.4"
            && result.Access == AccessStatus.Authenticated && fixture.Logins == 1, "Session or versions were not verified.");
        Check(result.Capabilities.SelectMany(cap => cap.Metrics).Any(metric => metric is { Name: "sampleLongestDownloadEtaSeconds", Value: 60 })
            && !JsonSerializer.Serialize(result).Contains("sensitive-tracker"), "Missing transfer metrics or exposed tracker data.");

        fixture.Bearer = true;
        fixture.UnknownState = true;
        result = await adapter.ObserveAsync(new(uri, Authentication.Bearer, null, "mock-bearer"), CancellationToken.None);
        Check(fixture.Logins == 1 && result.Compatibility == Compatibility.Partial
            && result.Capabilities.Any(cap => cap is { Key: "transferRates", Status: CapabilityStatus.Available }),
            "An unknown torrent state affected rates or triggered an unexpected login.");

        fixture.Denied = true;
        fixture.Bearer = false;
        result = await adapter.ObserveAsync(new(uri, Authentication.Session, "mock-user", "mock-password"), CancellationToken.None);
        Check(result.Access == AccessStatus.AuthenticationFailed && fixture.Logins == 2, "Failed login was not isolated.");
        Console.WriteLine("qBittorrent mock session and bearer credentials, sampled transfer metrics and tracker redaction passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        internal int Logins { get; private set; }
        internal bool Bearer { get; set; }
        internal bool UnknownState { get; set; }
        internal bool Denied { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Check(uri.Host == "downloads.example" && uri.AbsolutePath.StartsWith("/qbt/api/v2/", StringComparison.Ordinal),
                "qBittorrent request left its endpoint scope.");
            var endpoint = uri.AbsolutePath["/qbt/api/v2/".Length..];
            if (endpoint == "auth/login")
            {
                Logins++;
                Check(!Bearer && request.Method == HttpMethod.Post && request.Headers.Referrer?.Host == "downloads.example"
                    && await request.Content!.ReadAsStringAsync(cancellationToken) == "username=mock-user&password=mock-password",
                    "Unexpected authentication exchange.");
                var login = new HttpResponseMessage(Denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
                {
                    Content = new StringContent("Ok.")
                };
                login.Headers.TryAddWithoutValidation("Set-Cookie", "SID=mock-session; HttpOnly; Secure");
                return login;
            }

            Check(request.Method == HttpMethod.Get && (Bearer
                ? request.Headers.Authorization?.ToString() == "Bearer mock-bearer" && !request.Headers.Contains("Cookie")
                : request.Headers.GetValues("Cookie").Single() == "SID=mock-session"), "Incorrect download API authorization.");
            var body = endpoint switch
            {
                "app/version" => "v5.1.0",
                "app/webapiVersion" => "2.11.4",
                "transfer/info" => """{"dl_info_speed":100,"up_info_speed":200}""",
                "torrents/info" when uri.Query == "?limit=50" => UnknownState ? """[{"state":"future"}]"""
                    : """[{"state":"downloading","eta":60,"progress":0.7,"tracker":"sensitive-tracker"},{"state":"stalledDL"}]""",
                _ => throw new InvalidOperationException("Unexpected qBittorrent request.")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
