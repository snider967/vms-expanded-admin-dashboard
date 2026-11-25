using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.RemoteAdminDesk.MediaManagement;

internal sealed record StoredObservations(string ConnectionRevision, IntegrationObservation Latest,
    IntegrationObservation? LastUsable, DateTimeOffset? FirstFailureAt);

internal sealed class OperationalStore(string dataPath)
{
    internal const int SchemaVersion = 1;
    private const int MaxObservationBytes = 64 * 1024;
    private const int MaximumPages = 4096;
    private static readonly object InitializationGate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = PrivateMediaFiles.Root(dataPath);
    private string DirectoryPath => Path.Combine(_root, "state");
    private string DatabasePath => Path.Combine(DirectoryPath, "integrations.sqlite");

    // Call only on feature activation. A disabled install never creates this state.
    internal void Initialize()
    {
        lock (InitializationGate)
        {
            PrivateMediaFiles.EnsureDirectory(_root);
            PrivateMediaFiles.EnsureDirectory(DirectoryPath);
            var existed = EnsureDatabaseFile();
            using var connection = Open(DatabasePath);
            using (var pageSize = Command(connection, "PRAGMA page_size = 4096"))
                pageSize.ExecuteNonQuery();
            VerifySize(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            var version = ReadVersion(connection, transaction);
            if (version < 0 || version > SchemaVersion) throw new InvalidDataException("Unsupported operational state schema.");
            CheckIntegrity(connection, transaction);
            if (version == SchemaVersion)
            {
                using var verify = Command(connection, "SELECT service, observations FROM integration_state LIMIT 0", transaction);
                verify.ExecuteNonQuery();
                transaction.Commit();
                return;
            }

            using (var unknown = Command(connection, "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'", transaction))
                if (version == 0 && (long)unknown.ExecuteScalar()! != 0)
                    throw new InvalidDataException("Operational database has no recognized schema.");

            if (existed)
            {
                // The write reservation keeps a second connection from changing the source while
                // the backup connection takes its consistent SQLite snapshot.
                using var source = Open(DatabasePath, SqliteOpenMode.ReadOnly);
                Backup(source, Path.Combine(DirectoryPath, $"pre-migration-v{version}-to-v{SchemaVersion}.sqlite"));
            }

            using var migrate = Command(connection, """
                CREATE TABLE integration_state (
                    service TEXT PRIMARY KEY,
                    observations BLOB NOT NULL CHECK(length(observations) <= 65536)
                ) WITHOUT ROWID;
                PRAGMA user_version = 1;
                """, transaction);
            migrate.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    internal StoredObservations? Read(IntegrationId id)
    {
        using var connection = OpenCurrent(SqliteOpenMode.ReadOnly);
        using var select = Command(connection, """
            SELECT CASE WHEN length(observations) <= 65536 THEN observations ELSE -1 END
            FROM integration_state WHERE service = $service
            """);
        select.Parameters.AddWithValue("$service", ToKey(id));
        var result = select.ExecuteScalar();
        if (result is null) return null;
        if (result is not byte[] payload) throw new InvalidDataException("Operational observation exceeds its size limit.");
        if (payload.Length > MaxObservationBytes) throw new InvalidDataException("Operational observation exceeds its size limit.");
        var value = JsonSerializer.Deserialize<StoredObservations>(payload, Json);
        if (value?.Latest is null || value.Latest.Id != id || value.LastUsable is { } usable && usable.Id != id)
            throw new InvalidDataException("Operational observation is invalid.");
        return value;
    }

    internal bool Save(StoredObservations observations)
    {
        if (observations.LastUsable is { } usable && usable.Id != observations.Latest.Id)
            throw new ArgumentException("Observation services must agree.", nameof(observations));
        var payload = JsonSerializer.SerializeToUtf8Bytes(observations, Json);
        if (payload.Length > MaxObservationBytes) throw new InvalidDataException("Operational observation exceeds its size limit.");
        using var connection = OpenCurrent();
        using var upsert = Command(connection, """
            INSERT INTO integration_state(service, observations) VALUES ($service, $observations)
            ON CONFLICT(service) DO UPDATE SET observations = excluded.observations
            WHERE observations <> excluded.observations;
            """);
        upsert.Parameters.AddWithValue("$service", ToKey(observations.Latest.Id));
        upsert.Parameters.AddWithValue("$observations", payload);
        return upsert.ExecuteNonQuery() != 0;
    }

    internal void CreateRecoveryBackup()
    {
        lock (InitializationGate)
        {
            using var connection = OpenCurrent(SqliteOpenMode.ReadOnly);
            Backup(connection, Path.Combine(DirectoryPath, "recovery.sqlite"));
        }
    }

    private bool EnsureDatabaseFile()
    {
        try
        {
            PrivateMediaFiles.VerifyFile(DatabasePath);
            return true;
        }
        catch (FileNotFoundException)
        {
            using var stream = PrivateMediaFiles.CreateFile(DatabasePath);
            return false;
        }
    }

    private SqliteConnection OpenCurrent(SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        PrivateMediaFiles.VerifyDirectory(_root);
        PrivateMediaFiles.VerifyDirectory(DirectoryPath);
        var connection = Open(DatabasePath, mode);
        try
        {
            if (ReadVersion(connection) != SchemaVersion) throw new InvalidDataException("Unsupported operational state schema.");
            if (mode != SqliteOpenMode.ReadOnly)
            {
                using var limit = Command(connection, $"PRAGMA max_page_count = {MaximumPages}");
                if ((long)limit.ExecuteScalar()! > MaximumPages) throw new InvalidDataException("Operational database exceeds its page limit.");
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        PrivateMediaFiles.VerifyFile(path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 2
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Backup(SqliteConnection source, string target)
    {
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".backup-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (PrivateMediaFiles.CreateFile(temp)) { }
            using (var copy = Open(temp))
            {
                source.BackupDatabase(copy);
                CheckIntegrity(copy);
                if (ReadVersion(copy) != ReadVersion(source)) throw new InvalidDataException("Operational backup schema mismatch.");
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static void VerifySize(SqliteConnection connection)
    {
        using var pages = Command(connection, "PRAGMA page_count");
        using var size = Command(connection, "PRAGMA page_size");
        if ((long)pages.ExecuteScalar()! > MaximumPages || (long)size.ExecuteScalar()! != 4096)
            throw new InvalidDataException("Operational database exceeds expected storage limits.");
    }

    private static void CheckIntegrity(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, "PRAGMA quick_check(1)", transaction);
        if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new InvalidDataException("Operational database integrity check failed.");
    }

    private static long ReadVersion(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, "PRAGMA user_version", transaction);
        return (long)command.ExecuteScalar()!;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static string ToKey(IntegrationId id) => IntegrationRegistry.Resolve(id.ToString()).Key;
}
