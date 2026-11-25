using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.RemoteAdminDesk;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

internal static class MediaManagementTests
{
    internal static async Task Run(IApplicationPaths paths, ILibraryManager library)
    {
        var directory = Path.Combine(paths.DataPath, "remote-admin-desk", "media-management");
        var store = new MediaManagementSettings(paths.DataPath);
        var initial = store.ReadView();
        Check(!initial.Enabled && initial.Integrations.Length == 7 && initial.Integrations.All(entry => !entry.Enabled)
            && store.GetEnabledConnection(IntegrationId.Sonarr) is null && !Directory.Exists(directory),
            "A fresh read must be disabled and have no persistence side effects.");

        const string key = "private-fixture-sonarr";
        const string password = "private-fixture-password";
        const string username = "private-fixture-user";
        const string endpoint = "https://reader.example:9999/media/sonarr";
        store.SetIntegration("sonarr", true, endpoint, "apiKey", null, key, false, 300, 10);
        store.SetIntegration("qbittorrent", true, "http://[::1]:8080/qbt", "session", username, password, false, 120, 8);
        Check(store.GetEnabledConnection(IntegrationId.Sonarr) is null, "The master toggle must prevent connection acquisition.");
        store.SetEnabled(true);
        var afterRestart = new MediaManagementSettings(paths.DataPath);
        Check(afterRestart.GetEnabledConnection(IntegrationId.Sonarr)?.BaseUri.AbsoluteUri == endpoint + "/"
            && afterRestart.GetEnabledConnection(IntegrationId.QBittorrent)?.Username == username,
            "Saved endpoint prefixes and credentials must persist.");
        Check(afterRestart.GetEnabledConnection(IntegrationId.Radarr) is null, "Unconfigured integration must not obtain a connection.");
        var wire = JsonSerializer.Serialize(afterRestart.ReadView());
        Check(!wire.Contains(key) && !wire.Contains(password) && !wire.Contains(username),
            "Settings readback revealed authentication material.");
        store.SetIntegration("sonarr", false, endpoint, "apiKey", null, null, false, 300, 10);
        Check(store.GetEnabledConnection(IntegrationId.Sonarr) is null
            && store.GetEnabledConnection(IntegrationId.QBittorrent)?.Credential == password, "Enablement must be per service.");
        store.SetEnabled(false);
        store.SetEnabled(true);
        store.SetIntegration("sonarr", true, endpoint, "apiKey", null, null, false, 300, 10);
        Check(store.GetEnabledConnection(IntegrationId.Sonarr)?.Credential == key, "Reactivation must retain credentials.");
        Invalid(() => store.SetIntegration("sonarr", true, "https://different.example/", "apiKey", null, null, false, 300, 10));
        Invalid(() => store.SetIntegration("sonarr", true, endpoint, "basic", null, key, false, 300, 10));
        Invalid(() => store.SetIntegration("sonarr", true, endpoint, "apiKey", null, key, false, 1, 10));
        foreach (var bad in new[] { "file:///tmp", "https://u:p@example.org/api", "http://localhost:0", "https://example.org/?token=private",
                     "https://example.org/#fragment", "https://example.org/a%2fb", "https://example.org/a%2500" })
            Invalid(() => IntegrationEndpoint.Parse(bad));

        var config = Path.Combine(directory, "configuration.json");
        if (OperatingSystem.IsLinux()) CheckLinuxPermissions(directory, config, store);

        var controller = new DiagnosticsController(library, paths) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Check(typeof(DiagnosticsController).GetCustomAttribute<AuthorizeAttribute>()?.Policy == "RequiresElevation",
            "Jellyfin administrator policy must cover all Media Management routes.");
        Check(await controller.ReadMediaManagementSettings(CancellationToken.None) is ForbidResult
            && controller.ReadMediaManagementStatus() is ForbidResult
            && await controller.RefreshMediaManagement("sonarr", CancellationToken.None) is ForbidResult,
            "Anonymous caller accepted.");
        controller.HttpContext.User = Identity(isApiKey: true);
        Check(await controller.SetMediaManagementEnabled(new(false), CancellationToken.None) is ForbidResult
            && store.ReadView().Enabled, "An API key caller modified Media Management settings.");
        controller.HttpContext.User = Identity(isApiKey: false);
        Check(await controller.ReadMediaManagementSettings(CancellationToken.None) is JsonResult, "Elevated account path failed.");
        using var poller = new MediaPolling(paths.DataPath, [], TimeProvider.System);
        using var services = new ServiceCollection().AddSingleton(poller).BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        Check(controller.ReadMediaManagementStatus() is JsonResult cached
            && !JsonSerializer.Serialize(cached.Value, cached.SerializerSettings as JsonSerializerOptions).Contains(key),
            "Cache-only status route failed or included a secret.");
        Check(await controller.RefreshMediaManagement("sonarr", CancellationToken.None) is ConflictResult,
            "A refresh ran before a configured feature was loaded.");
        Check(await controller.SetMediaManagementIntegration("invalid", new(), CancellationToken.None) is BadRequestObjectResult,
            "An invalid service did not return a sanitized validation error.");
        Console.WriteLine("Media Management default-off gates, independent settings, credential persistence and route guards passed.");
    }

    [SupportedOSPlatform("linux")]
    private static void CheckLinuxPermissions(string directory, string config, MediaManagementSettings store)
    {
        Check(File.GetUnixFileMode(directory) == (UnixFileMode)448 && File.GetUnixFileMode(config) == (UnixFileMode)384,
            "Credential directory and file must be mode 0700 and 0600.");
        File.SetUnixFileMode(config, (UnixFileMode)420);
        try
        {
            try { store.ReadView(); throw new InvalidOperationException("Unsafe credential mode was accepted."); }
            catch (UnauthorizedAccessException) { }
        }
        finally { File.SetUnixFileMode(config, (UnixFileMode)384); }
    }

    private static ClaimsPrincipal Identity(bool isApiKey) => new(new ClaimsIdentity(
        [new("Jellyfin-UserId", Guid.NewGuid().ToString()), new("Jellyfin-IsApiKey", isApiKey.ToString())], "fixture"));

    private static void Invalid(Action operation)
    {
        try { operation(); }
        catch (InvalidMediaSettingsException) { return; }
        throw new InvalidOperationException("Invalid Media Management input was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
