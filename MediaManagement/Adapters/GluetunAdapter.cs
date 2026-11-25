using System.Net;
using System.Text.Json;
using static Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters.AdapterJson;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal sealed class GluetunAdapter(IntegrationHttp http) : IReadOnlyIntegrationAdapter
{
    private const int ResponseLimit = 16 * 1024;

    public IntegrationId Id => IntegrationId.Gluetun;

    public async Task<IntegrationObservation> ObserveAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        var probe = new IntegrationProbe(Id, "v1/", "v1", http, connection, cancellationToken);
        var identity = await probe.Read("vpn/status", ResponseLimit);
        using var status = identity.Document;
        if (IntegrationProbe.StopsObservation(identity.Failure))
            return probe.Create(null, ServiceHealth.Unknown, identity.Failure, versionOptional: true);

        var health = ServiceHealth.Unknown;
        probe.Add(status is null
            ? identity.Failure == ReadFailure.MissingEndpoint
                ? new("vpnStatus", CapabilityStatus.Unsupported, [], "endpoint-unsupported")
                : Invalid("vpnStatus")
            : ReadStatus(status.RootElement, out health));
        await probe.Collect("publicIp", "publicip/ip", ResponseLimit, ReadPublicIp);
        return probe.Create(null, health, identity.Failure, versionOptional: true);
    }

    private static CapabilityObservation ReadStatus(JsonElement root, out ServiceHealth health)
    {
        // The control API reports the requested VPN process state. Actual traffic
        // protection requires independent evidence beyond this read-only capability.
        health = ServiceHealth.Unknown;
        switch (Text(root, "status")?.ToLowerInvariant())
        {
            case "running":
                return Available("vpnStatus", new IntegrationMetric("controlReportsVpnRunning", 1));
            case "stopped":
                health = ServiceHealth.Degraded;
                return Available("vpnStatus", new IntegrationMetric("controlReportsVpnRunning", 0));
            default:
                return Partial("vpnStatus", "unknown-vpn-status");
        }
    }

    private static CapabilityObservation ReadPublicIp(JsonElement root)
    {
        var address = Text(root, "public_ip");
        return address is { Length: <= 64 } && IPAddress.TryParse(address, out _)
            ? Available("publicIp", new IntegrationMetric("exitIpReported", 1))
            : Partial("publicIp", "exit-ip-unavailable");
    }
}
