using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal static class GluetunAdapterTests
{
    internal static async Task Run()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var adapter = new GluetunAdapter(new(http));
        var endpoint = new Uri("https://vpn.example/gluetun/");
        var access = new IntegrationConnection(endpoint, Authentication.ApiKey, null, "fixture-key");
        var result = await adapter.ObserveAsync(access, CancellationToken.None);
        Check(result.ApiVersion == "v1" && result.ApplicationVersion is null && result.Compatibility == Compatibility.ApparentlyCompatible
            && result.Health == ServiceHealth.Unknown && result.Access == AccessStatus.Authenticated
            && !JsonSerializer.Serialize(result).Contains("203.0.113.5"), "Gluetun reachability overstated protection or exposed the exit address.");

        fixture.Basic = true;
        fixture.Stopped = true;
        fixture.NoExitIp = true;
        result = await adapter.ObserveAsync(new(endpoint, Authentication.Basic, "user", "secret"), CancellationToken.None);
        Check(result.Health == ServiceHealth.Degraded && result.Compatibility == Compatibility.Partial
            && result.Capabilities.Any(cap => cap is { Key: "publicIp", Status: CapabilityStatus.Unsupported }),
            "The stopped state or unsupported exit IP was misclassified.");

        fixture.Denied = true;
        result = await adapter.ObserveAsync(new(endpoint, Authentication.Basic, "user", "secret"), CancellationToken.None);
        Check(result.Access == AccessStatus.AuthorizationFailed && result.Health == ServiceHealth.Unknown,
            "Gluetun auth failure reported a VPN state.");
        Console.WriteLine("Gluetun mock control status, credentials, optional address endpoint and redaction passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        internal bool Basic { get; set; }
        internal bool Stopped { get; set; }
        internal bool NoExitIp { get; set; }
        internal bool Denied { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Check(request.Method == HttpMethod.Get && uri.Host == "vpn.example"
                && (Basic ? request.Headers.Authorization?.ToString() == "Basic dXNlcjpzZWNyZXQ="
                    : request.Headers.GetValues("X-Api-Key").Single() == "fixture-key"), "Gluetun requests must use read-only credentials.");
            if (Denied || (NoExitIp && uri.AbsolutePath.EndsWith("/publicip/ip", StringComparison.Ordinal)))
                return Task.FromResult(new HttpResponseMessage(Denied ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound));
            var body = uri.AbsolutePath switch
            {
                "/gluetun/v1/vpn/status" => Stopped ? """{"status":"stopped"}""" : """{"status":"running"}""",
                "/gluetun/v1/publicip/ip" => """{"public_ip":"203.0.113.5","country":"fixture"}""",
                _ => throw new InvalidOperationException("Unexpected control route.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
