using System.Text.Json;

namespace Jellyfin.Plugin.RemoteAdminDesk;

internal static class DeskSettings
{
    private const int MaxBytes = 24 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record Values(string? DiagnosticsWebhook = null, string? MediaWebhook = null,
        string? SonarrKey = null, string? RadarrKey = null, string? MediaBranches = null);
    internal sealed record Field(bool Configured, bool EnvironmentOverride);
    internal sealed record View(bool CanEdit, Field DiagnosticsWebhook, Field MediaWebhook, Field SonarrKey,
        Field RadarrKey, Field MediaBranches, string BranchPaths);

    internal static View Describe(Values saved)
    {
        static Field Status(string variable, string? value) =>
            new(!string.IsNullOrWhiteSpace(Effective(variable, value)), Override(variable));
        return new View(OperatingSystem.IsLinux(),
            Status(DeskDiscord.DiagnosticsVariable, saved.DiagnosticsWebhook),
            Status(DeskDiscord.MediaVariable, saved.MediaWebhook),
            Status("RAD_SONARR_API_KEY", saved.SonarrKey), Status("RAD_RADARR_API_KEY", saved.RadarrKey),
            Status("RAD_MEDIA_BRANCHES", saved.MediaBranches), Effective("RAD_MEDIA_BRANCHES", saved.MediaBranches) ?? "");
    }

    internal static string? Effective(string variable, string? saved)
    {
        var environment = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(environment) ? saved : environment;
    }

    private static bool Override(string variable) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable));

    internal static Values Read(string dataPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var stream = File.OpenRead(Path.Join(dataPath, "remote-admin-desk", "settings.json"));
            if (stream.Length > MaxBytes) throw new IOException("VMS settings exceed the size limit.");
            var data = new byte[(int)stream.Length];
            stream.ReadExactly(data);
            ct.ThrowIfCancellationRequested();
            return JsonSerializer.Deserialize<Values>(data, Json) ?? throw new JsonException("VMS settings are invalid.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Values();
        }
    }

    internal static View Update(string dataPath, Values changes, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new NotSupportedException("Web settings editing is available on Linux.");
        var current = Read(dataPath, ct);
        string? Apply(string variable, string? supplied, string? prior, Func<string, bool> validate)
        {
            if (supplied is null) return prior;
            if (Override(variable)) throw new ArgumentException(variable + " is set in the service environment.");
            if (string.IsNullOrWhiteSpace(supplied)) return null;
            if (!validate(supplied)) throw new ArgumentException("Invalid value for " + variable + ".");
            return supplied;
        }

        var next = new Values(
            Apply(DeskDiscord.DiagnosticsVariable, changes.DiagnosticsWebhook, current.DiagnosticsWebhook, DeskDiscord.IsWebhook),
            Apply(DeskDiscord.MediaVariable, changes.MediaWebhook, current.MediaWebhook, DeskDiscord.IsWebhook),
            Apply("RAD_SONARR_API_KEY", changes.SonarrKey, current.SonarrKey, ArrQueue.IsApiKey),
            Apply("RAD_RADARR_API_KEY", changes.RadarrKey, current.RadarrKey, ArrQueue.IsApiKey),
            Apply("RAD_MEDIA_BRANCHES", changes.MediaBranches, current.MediaBranches, ValidBranches));
        if (next == current) return Describe(next);

        var data = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        var directory = Path.Join(dataPath, "remote-admin-desk");
        Directory.CreateDirectory(directory);
        var target = Path.Join(directory, "settings.json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ct.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Access = FileAccess.Write, Mode = FileMode.CreateNew, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return Describe(next);
    }

    private static bool ValidBranches(string value)
    {
        var paths = value.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return paths.Length is >= 1 and <= 8 && paths.All(path =>
            path.Length <= 512 && Path.IsPathFullyQualified(path) && Path.GetFullPath(path) != "/"
            && !path.Any(char.IsControl));
    }
}
