using System.Text.Json;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal sealed class IntegrationProbe(IntegrationId id, string prefix, string apiVersion, IntegrationHttp http,
    IntegrationConnection connection, CancellationToken cancellationToken, string? session = null)
{
    private readonly List<CapabilityObservation> _capabilities = [];
    private bool _successfulRead;
    private bool _authenticatedRead;
    private ReadFailure? _denied;
    private TimeSpan? _retryAfter;
    private bool _rateLimited;

    internal static bool StopsObservation(ReadFailure? failure) => failure is ReadFailure.Authentication or ReadFailure.Authorization
        or ReadFailure.Network or ReadFailure.Timeout or ReadFailure.DestinationDenied or ReadFailure.RateLimited
        or ReadFailure.Redirect or ReadFailure.Unavailable;

    internal Task<(JsonDocument? Document, ReadFailure? Failure)> Read(string path, int maximum, bool authenticate = true) =>
        ReadCore(() => http.ReadJson(connection, prefix + path, maximum, cancellationToken, authenticate, session), authenticate);

    internal Task<(string? Value, ReadFailure? Failure)> ReadText(string path) =>
        ReadCore(() => http.ReadText(connection, prefix + path, cancellationToken, session), authenticate: true);

    private async Task<(T? Value, ReadFailure? Failure)> ReadCore<T>(Func<Task<T>> read, bool authenticate) where T : class
    {
        try
        {
            var body = await read();
            _successfulRead = true;
            _authenticatedRead |= authenticate;
            return (body, null);
        }
        catch (IntegrationReadException e)
        {
            if (authenticate && e.Failure is ReadFailure.Authentication or ReadFailure.Authorization) _denied ??= e.Failure;
            if (e.Failure == ReadFailure.RateLimited)
            {
                _rateLimited = true;
                _retryAfter = e.RetryAfter;
            }
            return (null, e.Failure);
        }
    }

    internal async Task Collect(string capability, string path, int maximum, Func<JsonElement, CapabilityObservation> parse)
    {
        if (_rateLimited)
        {
            _capabilities.Add(new(capability, CapabilityStatus.Unavailable, [], "rate-limited"));
            return;
        }
        var (document, failure) = await Read(path, maximum);
        using var body = document;
        _capabilities.Add(body is not null ? parse(body.RootElement) : new(capability, failure switch
        {
            ReadFailure.MissingEndpoint => CapabilityStatus.Unsupported,
            ReadFailure.InvalidResponse => CapabilityStatus.Incompatible,
            _ => CapabilityStatus.Unavailable
        }, [], Diagnostic(failure)));
    }

    internal void Add(CapabilityObservation observation) => _capabilities.Add(observation);

    internal IntegrationObservation Create(string? version, ServiceHealth health, ReadFailure? identityFailure, string? detectedApi = null, TimeSpan? retryAfter = null, bool versionOptional = false)
    {
        if (_capabilities.Count == 0)
            _capabilities.AddRange(IntegrationRegistry.Resolve(id.ToString()).Capabilities.Select(key =>
                new CapabilityObservation(key, identityFailure is ReadFailure.MissingEndpoint ? CapabilityStatus.Unsupported : CapabilityStatus.Unavailable,
                [], Diagnostic(identityFailure))));

        var connectionStatus = identityFailure switch
        {
            ReadFailure.Network => Connectivity.Unreachable,
            ReadFailure.Timeout => Connectivity.TimedOut,
            ReadFailure.DestinationDenied => Connectivity.Unknown,
            _ when _rateLimited || identityFailure == ReadFailure.RateLimited => Connectivity.RateLimited,
            _ => Connectivity.Reachable
        };
        var access = (_authenticatedRead ? null : _denied ?? identityFailure) switch
        {
            ReadFailure.Authentication => AccessStatus.AuthenticationFailed,
            ReadFailure.Authorization => AccessStatus.AuthorizationFailed,
            _ when _authenticatedRead && (version is not null || _capabilities.Any(cap => cap.Status is CapabilityStatus.Available or CapabilityStatus.Degraded))
                => AccessStatus.Authenticated,
            _ => AccessStatus.Unknown
        };
        var available = _capabilities.Count(capability => capability.Status is CapabilityStatus.Available or CapabilityStatus.Degraded);
        var compatibility = available > 0
            ? (version is not null || versionOptional) && _capabilities.All(cap => cap.Status == CapabilityStatus.Available)
                ? Compatibility.ApparentlyCompatible : Compatibility.Partial
            : _capabilities.All(cap => cap.Status == CapabilityStatus.Unsupported) && identityFailure == ReadFailure.MissingEndpoint
                ? Compatibility.Unsupported
                : _capabilities.Any(cap => cap.Status == CapabilityStatus.Incompatible) ? Compatibility.SuspectedIncompatibility : Compatibility.Unknown;
        return new(id, DateTimeOffset.UtcNow, connectionStatus, access, compatibility,
            health, version, detectedApi ?? (_successfulRead ? apiVersion : null), _capabilities.AsReadOnly(),
            _rateLimited ? "rate-limited" : Diagnostic(identityFailure ?? (_authenticatedRead ? null : _denied)) ?? (version is null && !versionOptional ? "version-unavailable" : null), _retryAfter ?? retryAfter);
    }

    private static string? Diagnostic(ReadFailure? failure) => failure switch
    {
        ReadFailure.Network => "network-unavailable",
        ReadFailure.Timeout => "request-timeout",
        ReadFailure.Authentication => "authentication-failed",
        ReadFailure.Authorization => "authorization-failed",
        ReadFailure.MissingEndpoint => "endpoint-unsupported",
        ReadFailure.InvalidResponse => "invalid-response",
        ReadFailure.ResponseLimit => "response-limit",
        ReadFailure.RateLimited => "rate-limited",
        ReadFailure.Redirect => "redirect-refused",
        ReadFailure.DestinationDenied => "destination-denied",
        ReadFailure.Unavailable => "service-unavailable",
        _ => null
    };
}
