using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;

internal static class MediaPollingTests
{
    internal static async Task Run()
    {
        var temp = Directory.CreateTempSubdirectory("vms-polling-");
        try
        {
            var clock = new Clock();
            var sonarr = new FakeAdapter(IntegrationId.Sonarr, clock);
            var radarr = new FakeAdapter(IntegrationId.Radarr, clock);
            using var polling = new MediaPolling(temp.FullName, [sonarr, radarr], clock);
            var settings = new MediaManagementSettings(temp.FullName);
            settings.SetIntegration("sonarr", true, "http://localhost:8989/", "apiKey", null, "fixture-key", false, 60, 1);
            settings.SetIntegration("radarr", true, "http://localhost:7878/", "apiKey", null, "fixture-key", false, 60, 1);
            polling.LoadConfiguration();
            await polling.PollDue(CancellationToken.None);
            Check(!polling.CachedView().Enabled && sonarr.Calls == 0 && radarr.Calls == 0
                && !Directory.Exists(Path.Combine(PrivateMediaFiles.Root(temp.FullName), "state")),
                "Disabled polling touched the external adapters or the operational store.");
            settings.SetEnabled(true);
            polling.ConfigurationChanged();
            polling.LoadConfiguration();
            await polling.PollDue(CancellationToken.None);
            var initial = polling.CachedView();
            Check(initial is { Enabled: true, PersistenceAvailable: true }
                && initial.Services.Length == 2 && sonarr.Calls == 1 && radarr.Calls == 1,
                "Optional configuration did not start independent polling.");
            _ = polling.CachedView();
            await polling.PollDue(CancellationToken.None);
            Check(sonarr.Calls == 1 && radarr.Calls == 1, "Warm cache or cadence caused another acquisition.");
            var benchmark = System.Diagnostics.Stopwatch.StartNew();
            for (var index = 0; index < 10_000; index++)
                Check(polling.CachedView().Services.Length == 2, "The cache lost a configured service.");
            benchmark.Stop();
            Check(sonarr.Calls == 1 && radarr.Calls == 1, "Cached reads triggered adapter calls.");
            Console.WriteLine($"In-process two-service cached view: 10,000 reads in {benchmark.Elapsed.TotalMilliseconds:F1} ms.");

            sonarr.Outage = true;
            clock.Now = clock.Now.AddMinutes(1);
            await polling.PollDue(CancellationToken.None);
            var failed = polling.CachedView().Services.Single(entry => entry.Id == "sonarr");
            Check(failed is { Stale: true, LastUsable.ApplicationVersion: "1.2.3", Latest.Connectivity: Connectivity.RateLimited }
                && failed.NextPollAt >= clock.Now.AddMinutes(5) && radarr.Calls == 2, "Partial outage lost cached information, backoff or isolation.");
            Check(await polling.Refresh(IntegrationId.Sonarr, manual: true, CancellationToken.None) == RefreshOutcome.Busy,
                "Manual refresh bypassed backoff.");

            using (var afterRestart = new MediaPolling(temp.FullName, [sonarr, radarr], clock))
            {
                afterRestart.LoadConfiguration();
                Check(afterRestart.CachedView().Services.Single(entry => entry.Id == "sonarr").LastUsable?.ApplicationVersion == "1.2.3",
                    "Last usable observation was lost after restart.");
            }

            clock.Now = clock.Now.AddMinutes(6);
            radarr.Block = true;
            var inFlight = polling.Refresh(IntegrationId.Radarr, manual: true, CancellationToken.None);
            await radarr.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(await polling.Refresh(IntegrationId.Radarr, manual: true, CancellationToken.None) == RefreshOutcome.Busy,
                "The same integration overlapped its in-flight work.");
            settings.SetEnabled(false);
            polling.ConfigurationChanged();
            polling.LoadConfiguration();
            Check(await inFlight == RefreshOutcome.Changed && !polling.CachedView().Enabled,
                "Disabling did not cancel the in-flight observation or mask the cache.");
            Console.WriteLine("Media polling disabled gate, cadence, stale cache, retry, restart, isolation and cancellation passed.");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeAdapter(IntegrationId id, Clock clock) : IReadOnlyIntegrationAdapter
    {
        public IntegrationId Id => id;
        internal int Calls { get; private set; }
        internal bool Outage { get; set; }
        internal bool Block { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IntegrationObservation> ObserveAsync(IntegrationConnection connection, CancellationToken cancellationToken)
        {
            Calls++;
            if (Block)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return Outage
                ? new(id, clock.GetUtcNow(), Connectivity.RateLimited, AccessStatus.Unknown, Compatibility.Unknown,
                    ServiceHealth.Unknown, null, null, [], "rate-limited", TimeSpan.FromMinutes(6))
                : new(id, clock.GetUtcNow(), Connectivity.Reachable, AccessStatus.Authenticated, Compatibility.ApparentlyCompatible,
                    ServiceHealth.Healthy, "1.2.3", "v3", [new("health", CapabilityStatus.Available, [])]);
        }
    }
}
