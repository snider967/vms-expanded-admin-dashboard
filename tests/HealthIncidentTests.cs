using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.RemoteAdminDesk;
using MediaBrowser.Common.Configuration;

internal static class HealthIncidentTests
{
    internal static void Run(IApplicationPaths paths, string statePath)
    {
        var journal = Path.Combine(statePath, "diagnostic-incidents.json");
        var tracker = typeof(ScheduledDiagnostics).Assembly.GetType(
            "Jellyfin.Plugin.RemoteAdminDesk.DiagnosticIncidents", throwOnError: true)!;
        var record = tracker.GetMethod("Record", BindingFlags.Static | BindingFlags.NonPublic)!;
        var start = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        void Observe(int hours, bool? low, bool? mount) =>
            record.Invoke(null, [paths, start.AddHours(hours), low, mount, CancellationToken.None]);

        JsonElement[] Read()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(journal));
            return document.RootElement.EnumerateArray().Select(incident => incident.Clone()).ToArray();
        }

        Observe(0, false, false);
        Check(!File.Exists(journal), "Healthy observations should not create an incident.");

        Observe(1, true, true);
        var opened = Read();
        Check(opened.Length == 2 && opened.All(i => i.GetProperty("openedAt").GetDateTimeOffset() == start.AddHours(1)),
            "Independent low-space and mount incidents should open at the first observed failure.");

        Observe(2, true, null);
        var repeated = Read();
        Check(repeated.Length == 2 && repeated[0].GetProperty("lastObservedAt").GetDateTimeOffset() == start.AddHours(2),
            "A repeated fault should update the saved incident.");
        Check(repeated[1].GetProperty("lastObservedAt").GetDateTimeOffset() == start.AddHours(1),
            "An incomplete observation should leave the mount incident as it was.");

        Observe(3, false, null);
        Observe(2, true, null);
        var partialRecovery = Read();
        var low = partialRecovery.Single(i => i.GetProperty("kind").GetString() == "low-space");
        Check(low.GetProperty("resolvedAt").GetDateTimeOffset() == start.AddHours(3),
            "A late fault should not undo an observed recovery.");
        var mount = partialRecovery.Single(i => i.GetProperty("kind").GetString() == "missing-mount");
        Check(mount.GetProperty("resolvedAt").ValueKind == JsonValueKind.Null,
            "A storage recovery should not close an unobserved mount incident.");

        for (var hour = 4; hour < 210; hour += 2)
        {
            Observe(hour, true, null);
            Observe(hour + 1, false, null);
        }
        var retained = Read();
        Check(retained.Length == 101 && retained.Count(i => i.GetProperty("resolvedAt").ValueKind == JsonValueKind.Null) == 1,
            "The journal should prune resolved history and keep the open incident.");
        Console.WriteLine("Health incident history, incomplete probes, recovery, and retention passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
