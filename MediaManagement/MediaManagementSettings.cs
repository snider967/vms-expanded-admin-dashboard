using System.Text.Json;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;

internal sealed class MediaManagementSettings(string dataPath)
{
    private const int SettingsVersion = 1;
    private const int MaxSettingsBytes = 64 * 1024;
    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory = PrivateMediaFiles.Root(dataPath);
    private string SettingsPath => Path.Combine(_directory, "configuration.json");

    private sealed record SavedSettings(int Version, bool Enabled, Dictionary<string, SavedIntegration> Integrations);
    private sealed record SavedIntegration(bool Enabled, string? BaseUrl, Authentication Authentication,
        string? Username, string? Credential, int PollSeconds, int TimeoutSeconds, string? Revision = null);

    internal sealed record SettingsView(bool Enabled, IntegrationView[] Integrations);
    internal sealed record IntegrationView(string Id, string Name, bool Enabled, string? BaseUrl, string Authentication,
        bool CredentialConfigured, int PollSeconds, int TimeoutSeconds, string[] AuthenticationModes, string[] Capabilities);

    internal SettingsView ReadView() => Describe(Read());

    internal SettingsView SetEnabled(bool enabled) => Update(current => current with { Enabled = enabled });

    internal SettingsView SetIntegration(string id, bool enabled, string? baseUrl, string authentication,
        string? username, string? credential, bool clearCredential, int pollSeconds, int timeoutSeconds)
    {
        var definition = IntegrationRegistry.Resolve(id);
        var scheme = definition.AuthenticationModes.FirstOrDefault(mode =>
            string.Equals(mode.ToString(), authentication, StringComparison.OrdinalIgnoreCase), (Authentication)(-1));
        if (!definition.AuthenticationModes.Contains(scheme))
            throw new InvalidMediaSettingsException("Unsupported authentication mode.");
        if (pollSeconds is < 60 or > 86400 || timeoutSeconds is < 1 or > 30)
            throw new InvalidMediaSettingsException("Polling interval or timeout is outside the supported range.");
        if (clearCredential && credential is not null || !ValidSecret(username, 256) || !ValidSecret(credential, 2048))
            throw new InvalidMediaSettingsException("Invalid credential update.");
        if (scheme is not (Authentication.Basic or Authentication.Session) && username is not null)
            throw new InvalidMediaSettingsException("The selected authentication mode does not use a username.");
        if (scheme is Authentication.Basic && username?.Contains(':') is true)
            throw new InvalidMediaSettingsException("The Basic authentication username may not contain a colon.");

        var endpoint = string.IsNullOrWhiteSpace(baseUrl) ? null : IntegrationEndpoint.Parse(baseUrl).AbsoluteUri;
        return Update(current =>
        {
            current.Integrations.TryGetValue(definition.Key, out var previous);
            var sameConnection = previous is not null && previous.Authentication == scheme && previous.BaseUrl == endpoint;
            var savedUsername = username ?? (sameConnection && !clearCredential ? previous!.Username : null);
            var savedCredential = clearCredential ? null : credential ?? (sameConnection ? previous!.Credential : null);
            if (enabled && (endpoint is null || savedCredential is null
                || (scheme is Authentication.Basic or Authentication.Session && savedUsername is null)))
                throw new InvalidMediaSettingsException("Configure an endpoint and the required credentials before enabling this integration.");

            var revision = sameConnection && previous!.Username == savedUsername && previous.Credential == savedCredential
                ? ConnectionRevision(previous) : Guid.NewGuid().ToString("N");
            current.Integrations[definition.Key] = new(enabled, endpoint, scheme, savedUsername, savedCredential, pollSeconds, timeoutSeconds, revision);
            return current;
        });
    }

    internal sealed record PollingTarget(IntegrationId Id, IntegrationConnection Connection, string Revision, TimeSpan Interval);
    internal sealed record PollingConfiguration(bool Enabled, PollingTarget[] Targets);

    internal IntegrationConnection? GetEnabledConnection(IntegrationId id) =>
        ReadPollingConfiguration().Targets.FirstOrDefault(target => target.Id == id)?.Connection;

    internal PollingConfiguration ReadPollingConfiguration()
    {
        var saved = Read();
        if (!saved.Enabled) return new(false, []);
        var targets = new List<PollingTarget>();
        foreach (var definition in IntegrationRegistry.Definitions)
        {
            if (saved.Integrations.TryGetValue(definition.Key, out var entry)
                && entry is { Enabled: true, BaseUrl: not null, Credential: not null })
            {
                targets.Add(new(definition.Id, new(IntegrationEndpoint.Parse(entry.BaseUrl), entry.Authentication,
                    entry.Username, entry.Credential, entry.TimeoutSeconds), ConnectionRevision(entry), TimeSpan.FromSeconds(entry.PollSeconds)));
            }
        }
        return new(true, targets.ToArray());
    }

    private static string ConnectionRevision(SavedIntegration entry) => entry.Revision
        ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(entry.Authentication + ":" + entry.BaseUrl)));

    private SavedSettings Read()
    {
        try
        {
            PrivateMediaFiles.VerifyDirectory(_directory);
            PrivateMediaFiles.VerifyFile(SettingsPath);
            using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxSettingsBytes) throw new InvalidDataException();
            var settings = JsonSerializer.Deserialize<SavedSettings>(stream, Json);
            return settings is { Version: SettingsVersion, Integrations: not null } && settings.Integrations.Values.All(entry => entry is not null)
                ? settings : throw new InvalidDataException();
        }
        catch (FileNotFoundException) { return new(SettingsVersion, false, new()); }
        catch (DirectoryNotFoundException) { return new(SettingsVersion, false, new()); }
    }

    private SettingsView Update(Func<SavedSettings, SavedSettings> apply)
    {
        lock (WriteGate)
        {
            var next = apply(Read());
            Save(next);
            return Describe(next);
        }
    }

    private void Save(SavedSettings settings)
    {
        PrivateMediaFiles.EnsureDirectory(_directory);
        var temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = PrivateMediaFiles.CreateFile(temporary))
            {
                JsonSerializer.Serialize(stream, settings, Json);
                if (stream.Length > MaxSettingsBytes) throw new InvalidDataException();
                stream.Flush(flushToDisk: true);
            }
            PrivateMediaFiles.VerifyFile(temporary);
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static SettingsView Describe(SavedSettings current) => new(current.Enabled,
        IntegrationRegistry.Definitions.Select(definition =>
        {
            current.Integrations.TryGetValue(definition.Key, out var entry);
            return new IntegrationView(definition.Key, definition.DisplayName, entry?.Enabled ?? false, entry?.BaseUrl,
                (entry?.Authentication ?? definition.AuthenticationModes[0]).ToString(), entry?.Credential is not null,
                entry?.PollSeconds ?? 300, entry?.TimeoutSeconds ?? 10,
                definition.AuthenticationModes.Select(mode => mode.ToString()).ToArray(), definition.Capabilities.ToArray());
        }).ToArray());

    private static bool ValidSecret(string? value, int maximum) =>
        value is null || (value.Length > 0 && value.Length <= maximum && !value.Any(char.IsControl));
}

internal static class IntegrationEndpoint
{
    internal static Uri Parse(string value)
    {
        // Keep configured prefixes intact and exclude embedded credentials or request parameters.
        if (value.Length > 512 || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace)
            || value.IndexOfAny(['\\', '?', '#']) >= 0
            || !Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.Port == 0
            || endpoint.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new InvalidMediaSettingsException("Enter an HTTP or HTTPS service base URL without embedded credentials or parameters.");

        foreach (var segment in endpoint.AbsolutePath.Split('/'))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".." || decoded.IndexOfAny(['/', '\\', '%']) >= 0 || decoded.Any(char.IsControl))
                throw new InvalidMediaSettingsException("The service URL contains an invalid path segment.");
        }

        return new Uri(endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/", UriKind.Absolute);
    }
}

internal sealed class InvalidMediaSettingsException(string message) : ArgumentException(message);
