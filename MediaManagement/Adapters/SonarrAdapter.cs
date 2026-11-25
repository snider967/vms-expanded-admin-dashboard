namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;

internal sealed class SonarrAdapter(IntegrationHttp http) : IReadOnlyIntegrationAdapter
{
    private static readonly ArrV3Read.Service Service = new(IntegrationId.Sonarr,
        "series?includeSeasonImages=false", "series", "monitoredSeries",
        "missingMonitoredEpisodes", "missingEpisodes", "includeUnknownSeriesItems");
    private readonly ArrV3Read _api = new(http);

    public IntegrationId Id => Service.Id;

    public Task<IntegrationObservation> ObserveAsync(IntegrationConnection connection, CancellationToken cancellationToken) =>
        _api.ObserveAsync(Service, connection, cancellationToken);
}
