using Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;
using Microsoft.Data.Sqlite;

internal static class OperationalStoreTests
{
    internal static void Run()
    {
        SQLitePCL.Batteries_V2.Init();
        using var fixture = new StoreFixture();
        var store = fixture.Store;
        store.Initialize();
        Check(store.Read(IntegrationId.Sonarr) is null, "Fresh operational data should be empty.");
        var healthy = Observe(IntegrationId.Sonarr, "4.0.fixture");
        var failed = healthy with { Connectivity = Connectivity.Unreachable, Compatibility = Compatibility.Unknown,
            ApplicationVersion = null, ObservedAt = healthy.ObservedAt.AddMinutes(12) };
        var sonarr = new StoredObservations("fixture-revision", failed, healthy, failed.ObservedAt);
        var radarr = new StoredObservations("another-revision", Observe(IntegrationId.Radarr, "6.0.fixture"), null, null);
        Check(store.Save(sonarr) && !store.Save(sonarr) && store.Save(radarr), "Upsert must skip unchanged observations.");
        Check(new OperationalStore(fixture.Path).Read(IntegrationId.Sonarr)?.LastUsable?.ApplicationVersion == "4.0.fixture"
            && store.Read(IntegrationId.Radarr)?.ConnectionRevision == "another-revision", "History or service isolation was lost.");

        store.CreateRecoveryBackup();
        using (var connection = Open(fixture.Database))
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM integration_state";
            command.ExecuteNonQuery();
        }
        Check(store.Read(IntegrationId.Sonarr)?.FirstFailureAt == failed.ObservedAt, "An interrupted transaction lost committed observations.");

        using var restored = new StoreFixture();
        restored.StageFile();
        File.Copy(System.IO.Path.Combine(fixture.State, "recovery.sqlite"), restored.Database, overwrite: true);
        restored.Store.Initialize();
        Check(restored.Store.Read(IntegrationId.Radarr)?.ConnectionRevision == "another-revision", "Backup restore lost a committed observation.");
        if (OperatingSystem.IsLinux())
            Check(File.GetUnixFileMode(fixture.Database) == (UnixFileMode)384
                && File.GetUnixFileMode(System.IO.Path.Combine(fixture.State, "recovery.sqlite")) == (UnixFileMode)384,
                "Operational database and backup must have mode 0600.");

        using var prior = new StoreFixture();
        prior.StageFile();
        using (var connection = Open(prior.Database))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 0";
            command.ExecuteNonQuery();
        }
        prior.Store.Initialize();
        using (var backup = Open(System.IO.Path.Combine(prior.State, "pre-migration-v0-to-v1.sqlite")))
        using (var command = backup.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version";
            Check((long)command.ExecuteScalar()! == 0 && prior.Store.Read(IntegrationId.Sonarr) is null,
                "Migration did not preserve a valid prior schema backup.");
        }

        using (var connection = Open(fixture.Database))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 99";
            command.ExecuteNonQuery();
        }
        var newer = File.ReadAllBytes(fixture.Database);
        ExpectInvalid(store.Initialize);
        ExpectInvalid(() => store.Save(sonarr));
        Check(newer.SequenceEqual(File.ReadAllBytes(fixture.Database)), "Unsupported schema was modified.");

        using var corrupt = new StoreFixture();
        corrupt.StageFile();
        File.WriteAllText(corrupt.Database, "not a database");
        try { corrupt.Store.Initialize(); throw new InvalidOperationException("Corrupt storage was silently recreated."); }
        catch (SqliteException) { }
        Check(File.ReadAllText(corrupt.Database) == "not a database", "Corrupt storage was overwritten.");

        Console.WriteLine("Operational SQLite persistence, idempotent updates, migration backup, restore and failure isolation passed.");
    }

    private static IntegrationObservation Observe(IntegrationId id, string version) => new(
        id, DateTimeOffset.UtcNow, Connectivity.Reachable, AccessStatus.Authenticated,
        Compatibility.ApparentlyCompatible, ServiceHealth.Healthy, version, "v3", []);

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void ExpectInvalid(Action operation)
    {
        try { operation(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("An incompatible operational schema was accepted.");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("vms-operational-");
        internal string Path => _fixture.FullName;
        internal string State => System.IO.Path.Combine(PrivateMediaFiles.Root(Path), "state");
        internal string Database => System.IO.Path.Combine(State, "integrations.sqlite");
        internal OperationalStore Store => new(Path);

        internal void StageFile()
        {
            PrivateMediaFiles.EnsureDirectory(PrivateMediaFiles.Root(Path));
            PrivateMediaFiles.EnsureDirectory(State);
            using var file = PrivateMediaFiles.CreateFile(Database);
        }

        public void Dispose() => _fixture.Delete(recursive: true);
    }
}
