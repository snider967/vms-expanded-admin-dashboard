using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.RemoteAdminDesk;

internal static class DiagnosticIncidents
{
    private const string FileName = "diagnostic-incidents.json";
    private const int ResolvedLimit = 100;
    internal const string LowSpace = "low-space";
    internal const string MissingMount = "missing-mount";

    internal static string Label(string kind) => kind switch
    {
        LowSpace => "Low media storage",
        MissingMount => "Media mount unavailable",
        _ => "Diagnostic incident"
    };

    internal sealed record Incident(string Kind, DateTimeOffset OpenedAt, DateTimeOffset LastObservedAt,
        DateTimeOffset? ResolvedAt = null);

    internal static Incident[] Read(IApplicationPaths paths, CancellationToken ct) =>
        DeskState.Read<Incident[]>(paths, FileName, ct) ?? [];

    internal static void Record(IApplicationPaths paths, DateTimeOffset observedAt, bool? lowSpace, bool? missingMount,
        CancellationToken ct)
    {
        if (lowSpace is null && missingMount is null) return;

        var prior = Read(paths, ct);
        var next = Observe(prior, LowSpace, lowSpace, observedAt);
        next = Observe(next, MissingMount, missingMount, observedAt);
        if (!ReferenceEquals(prior, next))
        {
            var retained = next.Where(incident => incident.ResolvedAt is null)
                .Concat(next.Where(incident => incident.ResolvedAt is not null)
                    .OrderByDescending(incident => incident.ResolvedAt).Take(ResolvedLimit))
                .ToArray();
            DeskState.Save(paths, FileName, retained, ct);
        }
    }

    private static Incident[] Observe(Incident[] history, string kind, bool? fault, DateTimeOffset observedAt)
    {
        if (fault is null) return history;

        var latest = history.Where(incident => incident.Kind == kind)
            .MaxBy(incident => incident.LastObservedAt);
        if (latest is not null && latest.LastObservedAt >= observedAt) return history;

        if (latest is null || latest.ResolvedAt is not null)
            return fault.Value ? [.. history, new Incident(kind, observedAt, observedAt)] : history;

        var updated = latest with { LastObservedAt = observedAt, ResolvedAt = fault.Value ? null : observedAt };
        return history.Select(incident => ReferenceEquals(incident, latest) ? updated : incident).ToArray();
    }
}
