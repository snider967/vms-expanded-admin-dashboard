using System.Text.Json;
using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement.Adapters;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;

internal enum RefreshOutcome { Completed, Disabled, Busy, Changed }

internal sealed class MediaPolling : BackgroundService
{
    private const int ParallelLimit = 2;
    private static readonly TimeSpan ConfigurationRetry = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _slots = new(ParallelLimit, ParallelLimit);
    private readonly MediaManagementSettings _settings;
    private readonly OperationalStore _store;
    private readonly IReadOnlyDictionary<IntegrationId, IReadOnlyIntegrationAdapter> _adapters;
    private readonly Dictionary<IntegrationId, State> _states = new();
    private readonly HashSet<IntegrationId> _running = [];
    private readonly TimeProvider _clock;
    private bool _enabled;
    private bool _loaded;
    private bool _settingsAvailable = true;
    private volatile bool _persistenceAvailable;
    private volatile bool _initialized;
    private CancellationTokenSource _configurationLifetime = new();

    private sealed class State(MediaManagementSettings.PollingTarget target)
    {
        internal MediaManagementSettings.PollingTarget Target { get; } = target;
        internal StoredObservations? Stored { get; set; }
        internal DateTimeOffset DueAt { get; set; }
        internal DateTimeOffset NextManualAt { get; set; }
        internal DateTimeOffset RateLimitUntil { get; set; }
        internal bool Running { get; set; }
        internal int ConsecutiveFailures { get; set; }
    }

    internal sealed record ServiceView(string Id, string State, bool Stale, IntegrationObservation? Latest,
        IntegrationObservation? LastUsable, DateTimeOffset? FirstFailureAt, DateTimeOffset? NextPollAt);
    internal sealed record PollingView(bool Enabled, bool SettingsAvailable, bool? PersistenceAvailable, ServiceView[] Services);

    internal MediaPolling(IApplicationPaths paths)
        : this(paths.DataPath, [new SonarrAdapter(IntegrationHttp.Shared), new RadarrAdapter(IntegrationHttp.Shared),
            new BazarrAdapter(IntegrationHttp.Shared), new ProwlarrAdapter(IntegrationHttp.Shared), new SeerrAdapter(IntegrationHttp.Shared),
            new QBittorrentAdapter(IntegrationHttp.Shared), new GluetunAdapter(IntegrationHttp.Shared)], TimeProvider.System)
    {
    }

    internal MediaPolling(string dataPath, IEnumerable<IReadOnlyIntegrationAdapter> adapters, TimeProvider clock)
    {
        _settings = new(dataPath);
        _store = new(dataPath);
        _adapters = adapters.ToDictionary(adapter => adapter.Id);
        _clock = clock;
    }

    internal void ConfigurationChanged()
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            _loaded = false;
            previous = _configurationLifetime;
            _configurationLifetime = new();
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        previous.Cancel();
        previous.Dispose();
    }

    internal bool IsEnabled
    {
        get { lock (_gate) return _loaded && _enabled; }
    }

    internal PollingView CachedView()
    {
        lock (_gate)
        {
            if (!_loaded || !_enabled) return new(false, _settingsAvailable, null, []);
            var now = _clock.GetUtcNow();
            return new(true, _settingsAvailable, _persistenceAvailable, _states.Values.Select(state =>
            {
                var latest = state.Stored?.Latest;
                var last = state.Stored?.LastUsable;
                var stale = last is not null && (latest is null || !HasUsableData(latest) || latest.Compatibility == Compatibility.Partial
                    || now - last.ObservedAt >= state.Target.Interval * 2);
                return new ServiceView(IntegrationRegistry.Resolve(state.Target.Id.ToString()).Key,
                    state.Running ? "refreshing" : latest is null ? "pending" : "observed", stale,
                    latest, last, state.Stored?.FirstFailureAt,
                    state.DueAt == default || state.DueAt == Suspended ? null : state.DueAt);
            }).ToArray());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!IsLoaded()) LoadConfiguration();
            await PollDue(stoppingToken);
            TimeSpan delay;
            lock (_gate)
            {
                delay = !_settingsAvailable ? ConfigurationRetry
                    : _loaded && _enabled && _states.Count > 0 ? TimeSpan.FromSeconds(15)
                    : Timeout.InfiniteTimeSpan;
            }
            var signaled = await _wake.WaitAsync(delay, stoppingToken);
            if (!signaled)
            {
                lock (_gate)
                    if (!_settingsAvailable) _loaded = false;
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        lock (_gate) _configurationLifetime.Dispose();
    }

    private bool IsLoaded()
    {
        lock (_gate) return _loaded;
    }

    internal void LoadConfiguration()
    {
        // Capture the generation to avoid publishing a file read superseded by a concurrent save.
        CancellationTokenSource generation;
        lock (_gate) generation = _configurationLifetime;
        MediaManagementSettings.PollingConfiguration configuration;
        try { configuration = _settings.ReadPollingConfiguration(); }
        catch (Exception e) when (StorageFailure(e) || e is InvalidMediaSettingsException)
        {
            lock (_gate)
            {
                if (generation != _configurationLifetime) return;
                _loaded = true;
                _enabled = false;
                _settingsAvailable = false;
            }
            return;
        }

        if (configuration.Enabled && configuration.Targets.Length > 0 && !_initialized)
        {
            try
            {
                _store.Initialize();
                _initialized = _persistenceAvailable = true;
            }
            catch (Exception e) when (StorageFailure(e)) { _persistenceAvailable = false; }
        }

        lock (_gate)
        {
            if (generation != _configurationLifetime) return;
            var old = new Dictionary<IntegrationId, State>(_states);
            _states.Clear();
            foreach (var target in configuration.Targets)
            {
                var state = new State(target);
                if (old.TryGetValue(target.Id, out var existing) && existing.Target.Revision == target.Revision)
                {
                    state.Stored = existing.Stored;
                    state.RateLimitUntil = existing.RateLimitUntil;
                    state.NextManualAt = existing.NextManualAt;
                    state.ConsecutiveFailures = existing.ConsecutiveFailures;
                    var adjusted = state.Stored is { } stored ? NextScheduled(stored.Latest, target.Interval) : default;
                    state.DueAt = adjusted > state.RateLimitUntil ? adjusted : state.RateLimitUntil;
                }
                _states.Add(target.Id, state);
            }
            _loaded = true;
            _enabled = configuration.Enabled;
            _settingsAvailable = true;
        }

        if (_initialized) RestoreMissing(generation);
    }

    private void RestoreMissing(CancellationTokenSource generation)
    {
        State[] missing;
        lock (_gate) missing = _states.Values.Where(state => state.Stored is null).ToArray();
        foreach (var state in missing)
        {
            try
            {
                var saved = _store.Read(state.Target.Id);
                if (saved?.ConnectionRevision != state.Target.Revision) continue;
                lock (_gate)
                {
                    if (generation != _configurationLifetime || state.Running || state.Stored is not null) continue;
                    state.Stored = saved;
                    state.DueAt = NextScheduled(saved.Latest, state.Target.Interval);
                    if (saved.Latest.RetryAfter is { } delay)
                    {
                        state.RateLimitUntil = saved.Latest.ObservedAt + LimitRetry(delay);
                        if (state.RateLimitUntil > state.DueAt) state.DueAt = state.RateLimitUntil;
                    }
                }
            }
            catch (Exception e) when (StorageFailure(e)) { _persistenceAvailable = false; }
        }
    }

    internal async Task PollDue(CancellationToken cancellationToken)
    {
        IntegrationId[] due;
        lock (_gate)
        {
            if (!_enabled || !_loaded) return;
            var now = _clock.GetUtcNow();
            due = _states.Where(pair => !pair.Value.Running && pair.Value.DueAt <= now).Select(pair => pair.Key).ToArray();
        }
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = ParallelLimit, CancellationToken = cancellationToken },
            async (id, token) => { await Refresh(id, manual: false, token); });
    }

    internal async Task<RefreshOutcome> Refresh(IntegrationId id, bool manual, CancellationToken cancellationToken)
    {
        State state;
        CancellationTokenSource budget;
        CancellationToken configurationToken;
        lock (_gate)
        {
            if (!_loaded || !_enabled || !_states.TryGetValue(id, out state!)) return RefreshOutcome.Disabled;
            var now = _clock.GetUtcNow();
            if (_running.Contains(id) || (manual && (state.NextManualAt > now || state.RateLimitUntil > now)) ||
                (!manual && state.DueAt > now) || !_slots.Wait(0)) return RefreshOutcome.Busy;
            state.Running = true;
            _running.Add(id);
            state.NextManualAt = now.AddSeconds(30);
            configurationToken = _configurationLifetime.Token;
            budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, configurationToken);
        }

        using (budget)
        {
            try
            {
                budget.CancelAfter(TimeSpan.FromSeconds(65));
                IntegrationObservation latest;
                try
                {
                    budget.Token.ThrowIfCancellationRequested();
                    latest = await _adapters[id].ObserveAsync(state.Target.Connection, budget.Token);
                }
                catch (OperationCanceledException) when (configurationToken.IsCancellationRequested)
                {
                    return RefreshOutcome.Changed;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    latest = Failure(id, Connectivity.TimedOut, "poll-timeout");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    latest = Failure(id, Connectivity.Unknown, "adapter-failure");
                }

                var hasUsableData = HasUsableData(latest);
                StoredObservations next;
                lock (_gate)
                {
                    if (configurationToken.IsCancellationRequested || !_loaded || !_enabled) return RefreshOutcome.Changed;
                    var previous = state.Stored;
                    var best = hasUsableData && (latest.Compatibility != Compatibility.Partial || previous?.LastUsable is null)
                        ? latest : previous?.LastUsable;
                    DateTimeOffset? failureAt = hasUsableData && latest.Compatibility != Compatibility.Partial
                        ? null : previous?.FirstFailureAt ?? latest.ObservedAt;
                    next = new(state.Target.Revision, latest, best, failureAt);
                    state.Stored = next;
                    UpdateSchedule(state, latest, hasUsableData);
                }

                if (_initialized && !configurationToken.IsCancellationRequested)
                {
                    try
                    {
                        _store.Save(next);
                        _persistenceAvailable = true;
                    }
                    catch (Exception e) when (StorageFailure(e)) { _persistenceAvailable = false; }
                }
                return RefreshOutcome.Completed;
            }
            finally
            {
                lock (_gate)
                {
                    state.Running = false;
                    _running.Remove(id);
                }
                _slots.Release();
            }
        }
    }

    private void UpdateSchedule(State state, IntegrationObservation latest, bool usable)
    {
        var successful = usable && latest.Compatibility != Compatibility.Partial;
        state.ConsecutiveFailures = successful ? 0 : Math.Min(state.ConsecutiveFailures + 1, 6);
        var delays = successful ? state.Target.Interval
            : TimeSpan.FromSeconds(Math.Max(state.Target.Interval.TotalSeconds, 30 * (1 << state.ConsecutiveFailures)));
        if (latest.Connectivity == Connectivity.RateLimited)
        {
            var retry = latest.RetryAfter is { } requested ? LimitRetry(requested) : TimeSpan.FromMinutes(1);
            state.RateLimitUntil = latest.ObservedAt + retry;
            if (retry > delays) delays = retry;
        }
        state.DueAt = CredentialsRejected(latest) ? Suspended : latest.ObservedAt + delays;
    }

    // Rejected credentials will not recover on their own, and repeated logins can trigger service lockouts
    // (qBittorrent bans an address after repeated failures by default). Pause scheduled polling until the
    // connection settings change (new revision) or a manual test succeeds.
    private static readonly DateTimeOffset Suspended = DateTimeOffset.MaxValue;

    private static bool CredentialsRejected(IntegrationObservation observation) =>
        observation.Access is AccessStatus.AuthenticationFailed or AccessStatus.AuthorizationFailed;

    private static DateTimeOffset NextScheduled(IntegrationObservation latest, TimeSpan interval) =>
        CredentialsRejected(latest) ? Suspended : latest.ObservedAt + interval;

    private IntegrationObservation Failure(IntegrationId id, Connectivity connectivity, string code) =>
        new(id, _clock.GetUtcNow(), connectivity, AccessStatus.Unknown, Compatibility.Unknown, ServiceHealth.Unknown,
            null, null, [], code);

    private static bool HasUsableData(IntegrationObservation observation) =>
        observation.Connectivity == Connectivity.Reachable
        && observation.Access is not (AccessStatus.AuthenticationFailed or AccessStatus.AuthorizationFailed)
        && observation.Capabilities.Any(cap => cap.Status == CapabilityStatus.Available
            || cap.Status == CapabilityStatus.Degraded && cap.Metrics.Count > 0);

    private static TimeSpan LimitRetry(TimeSpan retry) =>
        retry > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : retry;

    private static bool StorageFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or SqliteException or NotSupportedException;
}
