using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RemoteAdminDesk;

public sealed partial class DiagnosticsController
{
    private static readonly SemaphoreSlim MediaSettingsGate = new(1, 1);
    private static readonly JsonSerializerOptions MediaWireFormat = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private MediaPolling? MediaPoller => HttpContext.RequestServices?.GetService<MediaPolling>();

    [HttpGet("MediaManagement/Status")]
    public IActionResult ReadMediaManagementStatus()
    {
        if (!IsAccountAdministrator()) return Forbid();
        return MediaPoller is { } poller ? new JsonResult(poller.CachedView(), MediaWireFormat) : StatusCode(503);
    }

    [HttpPost("MediaManagement/{id}/Refresh")]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> RefreshMediaManagement(string id, CancellationToken cancellationToken)
    {
        if (!IsAccountAdministrator()) return Forbid();
        if (MediaPoller is not { } poller) return StatusCode(503);
        IntegrationDefinition service;
        try { service = IntegrationRegistry.Resolve(id); }
        catch (InvalidMediaSettingsException) { return NotFound(); }
        var outcome = await poller.Refresh(service.Id, manual: true, cancellationToken);
        return outcome switch
        {
            RefreshOutcome.Completed => new JsonResult(poller.CachedView(), MediaWireFormat),
            RefreshOutcome.Busy => StatusCode(429),
            _ => Conflict()
        };
    }


    [HttpGet("MediaManagement/Settings")]
    public Task<IActionResult> ReadMediaManagementSettings(CancellationToken cancellationToken) =>
        MediaSettings(settings => settings.ReadView(), cancellationToken);

    [HttpPut("MediaManagement/Settings")]
    [RequestSizeLimit(2048)]
    [Consumes("application/json")]
    public Task<IActionResult> SetMediaManagementEnabled([FromBody] MediaManagementChange change, CancellationToken cancellationToken) =>
        MediaSettings(settings => settings.SetEnabled(change.Enabled), cancellationToken, notify: true);

    [HttpPut("MediaManagement/Settings/{id}")]
    [RequestSizeLimit(16 * 1024)]
    [Consumes("application/json")]
    public Task<IActionResult> SetMediaManagementIntegration(string id, [FromBody] IntegrationChange change, CancellationToken cancellationToken) =>
        MediaSettings(settings => settings.SetIntegration(id, change.Enabled, change.BaseUrl, change.Authentication,
            change.Username, change.Credential, change.ClearCredential, change.PollSeconds, change.TimeoutSeconds), cancellationToken, notify: true);

    public sealed record MediaManagementChange(bool Enabled);
    public sealed class IntegrationChange
    {
        public bool Enabled { get; init; }
        public string? BaseUrl { get; init; }
        public string Authentication { get; init; } = "";
        public string? Username { get; init; }
        public string? Credential { get; init; }
        public bool ClearCredential { get; init; }
        public int PollSeconds { get; init; } = 300;
        public int TimeoutSeconds { get; init; } = 10;
    }

    private async Task<IActionResult> MediaSettings(Func<MediaManagementSettings, MediaManagementSettings.SettingsView> operation,
        CancellationToken cancellationToken, bool notify = false)
    {
        if (!IsAccountAdministrator()) return Forbid();
        try
        {
            var result = await DeskProbes.Run(MediaSettingsGate, TimeSpan.FromSeconds(3),
                _ =>
                {
                    var view = operation(new MediaManagementSettings(paths.DataPath));
                    if (notify) MediaPoller?.ConfigurationChanged();
                    return view;
                }, cancellationToken);
            return new JsonResult(result, WireFormat);
        }
        catch (InvalidMediaSettingsException e)
        {
            return BadRequest(new { message = e.Message });
        }
        catch (DeskProbeBusyException)
        {
            return StatusCode(429);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException or OperationCanceledException)
        {
            return StatusCode(503);
        }
    }
}
