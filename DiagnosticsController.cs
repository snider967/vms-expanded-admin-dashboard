using System.Text.Json;
using System.Security.Claims;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RemoteAdminDesk;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("RemoteAdminDesk")]
public sealed class DiagnosticsController(ILibraryManager library, IApplicationPaths paths) : ControllerBase
{
    private static readonly JsonSerializerOptions WireFormat = new(JsonSerializerDefaults.Web);
    private static readonly string? JellyfinVersion = typeof(DiagnosticsController).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(item => item.Key == "JellyfinVersion")?.Value;
    private readonly DeskProbes _probes = new(library, paths);
    private static readonly SemaphoreSlim WebhookTestGate = new(1, 1);

    [HttpGet("Access")]
    public IActionResult Access()
    {
        if (!IsAccountAdministrator()) return Forbid();
        return new JsonResult(new { jellyfinVersion = JellyfinVersion }, WireFormat);
    }

    [HttpGet("Storage")]
    public async Task<IActionResult> Storage(CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Forbid();
        if (_probes.CachedStorage is { } cached)
            return new JsonResult(cached, WireFormat);
        return await RunProbe(DeskProbes.StorageGate, _probes.ReadStorage, cancellationToken);
    }

    [HttpPost("Storage")]
    public Task<IActionResult> CheckStorage(CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Task.FromResult<IActionResult>(Forbid());
        return Respond(async () =>
        {
            var result = await DeskProbes.Run(DeskProbes.StorageGate, TimeSpan.FromSeconds(3), _probes.ReadStorage, cancellationToken);
            if (result.Problem is null)
                await DeskProbes.Run(DeskProbes.StateGate, TimeSpan.FromSeconds(3),
                    ct => DashboardSnapshot.UpdateChecks(paths, ct, storage: result), cancellationToken, waitForSlot: true);
            return result;
        });
    }

    [HttpGet("Dashboard")]
    public Task<IActionResult> Dashboard(CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Task.FromResult<IActionResult>(Forbid());
        return RunProbe(DeskProbes.StateGate, ct =>
        {
            var checks = DashboardSnapshot.ReadChecks(paths, ct);
            return new {
                checks.Storage, checks.Branches, checks.Media,
                StorageHistory = DashboardSnapshot.MergeStorageHistory(checks, DeskState.ReadDiagnostics(paths, ct)),
                Charts = DashboardSnapshot.ReadCharts(paths, ct)
            };
        }, cancellationToken);
    }

    [HttpGet("Logs")]
    public async Task<IActionResult> Logs(CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Forbid();
        if (_probes.CachedCatalog is { } cached)
            return new JsonResult(cached, WireFormat);
        return await RunProbe(DeskProbes.LogsGate, _probes.ReadCatalog, cancellationToken);
    }

    [HttpGet("LogTail")]
    public async Task<IActionResult> LogTail([FromQuery] string name, CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Forbid();
        if (!DeskProbes.IsLogName(name)) return BadRequest();
        if (_probes.GetCachedLog(name) is { } cached)
            return new JsonResult(cached, WireFormat);
        return await RunProbe(DeskProbes.LogsGate, ct => _probes.ReadTail(name, ct), cancellationToken);
    }

    [HttpGet("Samples")]
    public async Task<IActionResult> Samples(CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Forbid();
        return await RunProbe(DeskProbes.StateGate, ct => DeskState.ReadDiagnostics(paths, ct), cancellationToken);
    }

    [HttpGet("Notifications")]
    public async Task<IActionResult> Notifications(CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Forbid();
        return await RunProbe(DeskProbes.StateGate, ct => DeskState.ReadNotifications(paths, ct), cancellationToken);
    }

    [HttpPost("Notifications/Test/{channel}")]
    public Task<IActionResult> TestNotification(string channel, CancellationToken ct)
    {
        if (!IsAccountAdministrator()) return Task.FromResult<IActionResult>(Forbid());
        if (channel is not ("diagnostics" or "media")) return Task.FromResult<IActionResult>(NotFound());
        return Respond(async () =>
        {
            if (!WebhookTestGate.Wait(0, ct)) throw new DeskProbeBusyException();
            try
            {
                var settings = await DeskProbes.Run(DeskProbes.StateGate, TimeSpan.FromSeconds(3),
                    token => DeskSettings.Read(paths.DataPath, token), ct, waitForSlot: true);
                var diagnostics = channel == "diagnostics";
                var webhook = diagnostics
                    ? DeskSettings.Effective(DeskDiscord.DiagnosticsVariable, settings.DiagnosticsWebhook)
                    : DeskSettings.Effective(DeskDiscord.MediaVariable, settings.MediaWebhook);
                var label = diagnostics ? "Diagnostics" : "New movies and TV";
                var status = DeskDiscord.Configuration(webhook) == "missing"
                    ? "missing"
                    : await DeskDiscord.Send(webhook, "VMS · " + label + " webhook test", ct);
                return new { status, testedAt = DateTimeOffset.UtcNow };
            }
            finally
            {
                WebhookTestGate.Release();
            }
        });
    }

    [HttpGet("Imports")]
    public Task<IActionResult> Imports(CancellationToken cancellationToken) =>
        ReadArr(ArrQueue.Read, cancellationToken);

    [HttpGet("ImportHistory")]
    public Task<IActionResult> ImportHistory(CancellationToken cancellationToken) =>
        ReadArr((settings, ct) => ArrHistory.Read(settings, library, ct), cancellationToken);

    private async Task<IActionResult> ReadArr<T>(Func<DeskSettings.Values, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Forbid();
        try
        {
            var saved = await DeskProbes.Run(DeskProbes.StateGate, TimeSpan.FromSeconds(3),
                ct => DeskSettings.Read(paths.DataPath, ct), cancellationToken);
            return new JsonResult(await read(saved, cancellationToken), WireFormat);
        }
        catch (DeskProbeBusyException)
        {
            return StatusCode(429);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            return StatusCode(503);
        }
    }

    [HttpGet("PlaybackTriage")]
    public async Task<IActionResult> PlaybackTriage(CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Forbid();
        return await RunProbe(DeskProbes.LogsGate, _probes.ReadPlaybackTriage, cancellationToken);
    }

    [HttpGet("Branches")]
    public async Task<IActionResult> Branches(CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Forbid();
        return await RunProbe(DeskProbes.StorageGate, ct => _probes.ReadBranches(
            DeskSettings.Effective("RAD_MEDIA_BRANCHES", DeskSettings.Read(paths.DataPath, ct).MediaBranches), ct), cancellationToken);
    }

    [HttpPost("Branches")]
    public Task<IActionResult> CheckBranches(CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Task.FromResult<IActionResult>(Forbid());
        return Respond(async () =>
        {
            var settings = await DeskProbes.Run(DeskProbes.StateGate, TimeSpan.FromSeconds(3),
                ct => DeskSettings.Read(paths.DataPath, ct), cancellationToken);
            var result = await DeskProbes.Run(DeskProbes.StorageGate, TimeSpan.FromSeconds(3), ct =>
                new DashboardSnapshot.TimedBranches(DateTimeOffset.UtcNow, _probes.ReadBranches(
                    DeskSettings.Effective("RAD_MEDIA_BRANCHES", settings.MediaBranches), ct)), cancellationToken);
            if (result.Result.Problem is null)
                await DeskProbes.Run(DeskProbes.StateGate, TimeSpan.FromSeconds(3),
                    ct => DashboardSnapshot.UpdateChecks(paths, ct, branches: result), cancellationToken, waitForSlot: true);
            return result;
        });
    }

    [HttpGet("Settings")]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Forbid();
        return await RunProbe(DeskProbes.StateGate, ct => DeskSettings.Describe(DeskSettings.Read(paths.DataPath, ct)),
            cancellationToken);
    }

    [HttpPut("Settings")]
    [RequestSizeLimit(16 * 1024)]
    [Consumes("application/json")]
    public async Task<IActionResult> SaveSettings([FromBody] SettingsChange change, CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Forbid();
        if (!OperatingSystem.IsLinux()) return StatusCode(501);
        try
        {
            var settings = await DeskProbes.Run(DeskProbes.StateGate, TimeSpan.FromSeconds(5), ct =>
                DeskSettings.Update(paths.DataPath, new DeskSettings.Values(change.DiagnosticsWebhook, change.MediaWebhook,
                    change.SonarrKey, change.RadarrKey, change.MediaBranches), ct), cancellationToken);
            return new JsonResult(settings, WireFormat);
        }
        catch (ArgumentException e)
        {
            // Validation errors from the settings service contain only the setting name.
            return BadRequest(new { message = e.ParamName is null && e.GetType() == typeof(ArgumentException)
                && (e.Message.StartsWith("Invalid value for RAD_", StringComparison.Ordinal)
                    || e.Message.StartsWith("RAD_", StringComparison.Ordinal))
                ? e.Message : "Invalid settings." });
        }
        catch (DeskProbeBusyException)
        {
            return StatusCode(429);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            return StatusCode(503);
        }
    }

    public sealed record SettingsChange(string? DiagnosticsWebhook, string? MediaWebhook,
        string? SonarrKey, string? RadarrKey, string? MediaBranches);

    private bool IsAccountAdministrator() =>
        Guid.TryParse(User.FindFirstValue("Jellyfin-UserId"), out var id) && id != Guid.Empty
        && bool.TryParse(User.FindFirstValue("Jellyfin-IsApiKey"), out var isApiKey) && !isApiKey;

    private Task<IActionResult> RunProbe<T>(
        SemaphoreSlim gate, Func<CancellationToken, T> probe, CancellationToken cancellationToken) =>
        Respond(() => DeskProbes.Run(gate, TimeSpan.FromSeconds(3), probe, cancellationToken));

    private async Task<IActionResult> Respond<T>(Func<Task<T>> operation)
    {
        try
        {
            var result = await operation();
            return new JsonResult(result, WireFormat);
        }
        catch (DeskProbeBusyException)
        {
            return StatusCode(429);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(503);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
            return StatusCode(503);
        }
    }
}
