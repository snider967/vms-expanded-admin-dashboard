namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;

internal enum IntegrationId { Sonarr, Radarr, Bazarr, Prowlarr, Seerr, QBittorrent, Gluetun }
internal enum Authentication { ApiKey, Session, Bearer, Basic }
internal enum Connectivity { Unknown, Reachable, Unreachable, TimedOut, RateLimited }
internal enum AccessStatus { Unknown, Authenticated, AuthenticationFailed, AuthorizationFailed }
internal enum Compatibility { Unknown, Tested, ApparentlyCompatible, Partial, Unsupported, SuspectedIncompatibility, Incompatible }
internal enum CapabilityStatus { Pending, Available, Degraded, Unsupported, Unavailable, Incompatible }
internal enum ServiceHealth { Unknown, Healthy, Degraded }

internal sealed record IntegrationDefinition(
    IntegrationId Id, string DisplayName, Authentication[] AuthenticationModes, string[] Capabilities)
{
    internal string Key => Id.ToString().ToLowerInvariant();
}

internal static class IntegrationRegistry
{
    internal static IReadOnlyList<IntegrationDefinition> Definitions { get; } = Array.AsReadOnly<IntegrationDefinition>(
    [
        new(IntegrationId.Sonarr, "Sonarr", [Authentication.ApiKey], ["health", "series", "missingEpisodes", "queue"]),
        new(IntegrationId.Radarr, "Radarr", [Authentication.ApiKey], ["health", "movies", "missingMovies", "queue"]),
        new(IntegrationId.Bazarr, "Bazarr", [Authentication.ApiKey], ["health", "subtitles", "providers", "activity"]),
        new(IntegrationId.Prowlarr, "Prowlarr", [Authentication.ApiKey], ["health", "indexers", "indexerStatus", "statistics", "applications"]),
        new(IntegrationId.Seerr, "Seerr", [Authentication.ApiKey], ["health", "requests", "requestFailures"]),
        new(IntegrationId.QBittorrent, "qBittorrent", [Authentication.Session, Authentication.Bearer], ["transfers", "transferRates"]),
        new(IntegrationId.Gluetun, "Gluetun", [Authentication.ApiKey, Authentication.Basic], ["vpnStatus", "publicIp"])
    ]);

    internal static IntegrationDefinition Resolve(string key) => Definitions.FirstOrDefault(
        definition => string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidMediaSettingsException("Unknown integration.");
}

// Adapter output contains only explicitly selected observations. Never include raw payloads or exceptions.
internal sealed record IntegrationMetric(string Name, double Value);
internal sealed record CapabilityObservation(
    string Key, CapabilityStatus Status, IReadOnlyList<IntegrationMetric> Metrics, string? DiagnosticCode = null);
internal sealed record IntegrationObservation(
    IntegrationId Id, DateTimeOffset ObservedAt, Connectivity Connectivity, AccessStatus Access,
    Compatibility Compatibility, ServiceHealth Health, string? ApplicationVersion, string? ApiVersion,
    IReadOnlyList<CapabilityObservation> Capabilities, string? DiagnosticCode = null, TimeSpan? RetryAfter = null);

internal interface IReadOnlyIntegrationAdapter
{
    IntegrationId Id { get; }

    Task<IntegrationObservation> ObserveAsync(IntegrationConnection connection, CancellationToken cancellationToken);
}

internal sealed class IntegrationConnection(Uri baseUri, Authentication authentication, string? username, string credential, int timeoutSeconds = 10)
{
    internal Uri BaseUri { get; } = baseUri;
    internal Authentication Authentication { get; } = authentication;
    internal string? Username { get; } = username;
    internal string Credential { get; } = credential;
    internal TimeSpan Timeout { get; } = TimeSpan.FromSeconds(timeoutSeconds);
}
