using System.Text.Json;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal static class AdapterJson
{
    internal static JsonElement Property(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) ? value : default;

    internal static int? Count(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var count) && count >= 0 ? count : null;

    internal static string? Text(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static string? SafeVersion(string? version, string credential) => version is { Length: >= 3 and <= 64 }
        && char.IsAsciiDigit(version.TrimStart('v', 'V').FirstOrDefault()) && version.Contains('.') && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-')
        && !string.Equals(version, credential, StringComparison.Ordinal) ? version : null;

    internal static CapabilityObservation Available(string id, params IntegrationMetric[] metrics) =>
        new(id, CapabilityStatus.Available, metrics);

    internal static CapabilityObservation Invalid(string id) => new(id, CapabilityStatus.Incompatible, [], "invalid-response");

    internal static CapabilityObservation Partial(string id, string diagnostic, params IntegrationMetric[] metrics) =>
        new(id, CapabilityStatus.Degraded, metrics, diagnostic);

    internal static CapabilityObservation ReadArrHealth(JsonElement root, out ServiceHealth health)
    {
        health = ServiceHealth.Unknown;
        if (root.ValueKind != JsonValueKind.Array) return Invalid("health");
        var warnings = 0;
        var errors = 0;
        var unknown = false;
        foreach (var row in root.EnumerateArray())
        {
            switch (Text(row, "type")?.ToLowerInvariant())
            {
                case "warning": warnings++; break;
                case "error": errors++; break;
                case "ok":
                case "notice": break;
                default: unknown = true; break;
            }
        }
        health = warnings > 0 || errors > 0 ? ServiceHealth.Degraded : unknown ? ServiceHealth.Unknown : ServiceHealth.Healthy;
        return unknown ? Partial("health", "unknown-health-level")
            : Available("health", new("warnings", warnings), new("errors", errors));
    }
}
