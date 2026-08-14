using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Talon.Vfs;

// Owns the persistent dat_db.db schema and transactions. VfsCensus serializes
// all calls to this class on its background writer.
internal sealed class VfsCensusDatabase
{
    internal const int SchemaVersion = 1;
    private static readonly object SqliteInitializationLock = new();
    private static int sqliteInitialized;

    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset sessionStarted = DateTimeOffset.UtcNow;
    private int databaseInitialized;

    public VfsCensusDatabase(string? outputPath)
    {
        InitializeSqlite();
        OutputPath = Path.GetFullPath(outputPath ?? GetDefaultOutputPath());
        var directory = Path.GetDirectoryName(OutputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    public string OutputPath { get; }

    public static string GetDefaultOutputPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Galapa",
        "DQX",
        "dat_db.db");

    public void Write(IReadOnlyList<VfsCensusEntrySnapshot> snapshots)
    {
        using var connection = OpenConnection();
        EnsureDatabase(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureSession(connection, transaction);
        WriteSnapshots(connection, transaction, snapshots);
        transaction.Commit();
    }

    public void CompleteSession()
    {
        using var connection = OpenConnection();
        EnsureDatabase(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureSession(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE talon_sessions SET ended_at = $ended WHERE session_id = $session";
        command.Parameters.AddWithValue("$ended", FormatTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$session", sessionId);
        command.ExecuteNonQuery();
        transaction.Commit();

        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        checkpoint.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = OutputPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "PRAGMA busy_timeout=1000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void EnsureDatabase(SqliteConnection connection)
    {
        if (Volatile.Read(ref databaseInitialized) != 0) return;

        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL";
            journal.ExecuteScalar();
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        using var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
            throw new InvalidOperationException(
                $"VFS census database schema {version} is newer than supported schema {SchemaVersion}.");

        using (var migration = connection.CreateCommand())
        {
            migration.Transaction = transaction;
            migration.CommandText = SchemaVersionOne;
            migration.ExecuteNonQuery();
        }
        if (version < SchemaVersion)
        {
            using var updateVersion = connection.CreateCommand();
            updateVersion.Transaction = transaction;
            updateVersion.CommandText = $"PRAGMA user_version = {SchemaVersion}";
            updateVersion.ExecuteNonQuery();
        }
        transaction.Commit();
        Volatile.Write(ref databaseInitialized, 1);
    }

    private void EnsureSession(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO talon_sessions (
                session_id, process_id, started_at, ended_at)
            VALUES ($session, $process, $started, NULL)
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$process", Environment.ProcessId);
        command.Parameters.AddWithValue("$started", FormatTime(sessionStarted));
        command.ExecuteNonQuery();
    }

    private void WriteSnapshots(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<VfsCensusEntrySnapshot> snapshots)
    {
        using var insertPath = CreatePathCommand(connection, transaction);
        using var upsertObservation = CreateObservationCommand(connection, transaction);
        foreach (var snapshot in snapshots)
        {
            SetPathParameters(insertPath, snapshot);
            insertPath.ExecuteNonQuery();
            foreach (var resolution in snapshot.Resolutions)
            {
                SetObservationParameters(upsertObservation, snapshot, resolution);
                upsertObservation.ExecuteNonQuery();
            }
        }
    }

    private static SqliteCommand CreatePathCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO files (file, directory, file_hash, dir_hash, blowfish_key)
            SELECT $file, $directory, $file_hash, $dir_hash, NULL
            WHERE NOT EXISTS (
                SELECT 1 FROM files
                WHERE dir_hash = $dir_hash AND file_hash = $file_hash)
            """;
        command.Parameters.Add("$file", SqliteType.Text);
        command.Parameters.Add("$directory", SqliteType.Text);
        command.Parameters.Add("$file_hash", SqliteType.Text);
        command.Parameters.Add("$dir_hash", SqliteType.Text);
        return command;
    }

    private SqliteCommand CreateObservationCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO talon_vfs_observations (
                session_id, path, directory, file, dir_hash, file_hash,
                expansion, mount, outcome, count, first_seen, last_seen,
                replacement_bytes)
            VALUES (
                $session, $path, $directory, $file, $dir_hash, $file_hash,
                $expansion, $mount, $outcome, $count, $first_seen, $last_seen,
                $replacement_bytes)
            ON CONFLICT (session_id, path, expansion, mount, outcome) DO UPDATE SET
                count = excluded.count,
                first_seen = excluded.first_seen,
                last_seen = excluded.last_seen,
                replacement_bytes = excluded.replacement_bytes
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters.Add("$directory", SqliteType.Text);
        command.Parameters.Add("$file", SqliteType.Text);
        command.Parameters.Add("$dir_hash", SqliteType.Text);
        command.Parameters.Add("$file_hash", SqliteType.Text);
        command.Parameters.Add("$expansion", SqliteType.Integer);
        command.Parameters.Add("$mount", SqliteType.Integer);
        command.Parameters.Add("$outcome", SqliteType.Text);
        command.Parameters.Add("$count", SqliteType.Integer);
        command.Parameters.Add("$first_seen", SqliteType.Text);
        command.Parameters.Add("$last_seen", SqliteType.Text);
        command.Parameters.Add("$replacement_bytes", SqliteType.Integer);
        return command;
    }

    private static void SetPathParameters(SqliteCommand command, VfsCensusEntrySnapshot snapshot)
    {
        command.Parameters["$file"].Value = snapshot.File;
        command.Parameters["$directory"].Value = snapshot.Directory;
        command.Parameters["$file_hash"].Value = snapshot.FileHash;
        command.Parameters["$dir_hash"].Value = snapshot.DirectoryHash;
    }

    private static void SetObservationParameters(
        SqliteCommand command,
        VfsCensusEntrySnapshot snapshot,
        VfsResolutionSnapshot resolution)
    {
        command.Parameters["$path"].Value = snapshot.Path;
        command.Parameters["$directory"].Value = snapshot.Directory;
        command.Parameters["$file"].Value = snapshot.File;
        command.Parameters["$dir_hash"].Value = snapshot.DirectoryHash;
        command.Parameters["$file_hash"].Value = snapshot.FileHash;
        command.Parameters["$expansion"].Value = resolution.Expansion;
        command.Parameters["$mount"].Value = resolution.Mount;
        command.Parameters["$outcome"].Value = resolution.Outcome;
        command.Parameters["$count"].Value = resolution.Count;
        command.Parameters["$first_seen"].Value = FormatTime(resolution.FirstSeen);
        command.Parameters["$last_seen"].Value = FormatTime(resolution.LastSeen);
        command.Parameters["$replacement_bytes"].Value =
            resolution.ReplacementBytes.HasValue
                ? resolution.ReplacementBytes.Value
                : DBNull.Value;
    }

    private static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void InitializeSqlite()
    {
        if (Volatile.Read(ref sqliteInitialized) != 0) return;
        lock (SqliteInitializationLock)
        {
            if (sqliteInitialized != 0) return;
            SQLitePCL.Batteries_V2.Init();
            Volatile.Write(ref sqliteInitialized, 1);
        }
    }

    private const string SchemaVersionOne = """
        CREATE TABLE IF NOT EXISTS files (
            file TEXT,
            directory TEXT,
            file_hash TEXT,
            dir_hash TEXT,
            blowfish_key TEXT,
            UNIQUE (dir_hash, file_hash)
        );
        CREATE INDEX IF NOT EXISTS idx_file_hash ON files(file_hash);
        CREATE INDEX IF NOT EXISTS idx_dir_hash ON files(dir_hash);
        CREATE INDEX IF NOT EXISTS idx_file_dir ON files(file_hash, dir_hash);

        CREATE TABLE IF NOT EXISTS talon_sessions (
            session_id TEXT PRIMARY KEY COLLATE BINARY,
            process_id INTEGER NOT NULL,
            started_at TEXT NOT NULL,
            ended_at TEXT
        );

        CREATE TABLE IF NOT EXISTS talon_vfs_observations (
            session_id TEXT NOT NULL,
            path TEXT NOT NULL COLLATE BINARY,
            directory TEXT NOT NULL,
            file TEXT NOT NULL,
            dir_hash TEXT NOT NULL,
            file_hash TEXT NOT NULL,
            expansion INTEGER NOT NULL,
            mount INTEGER NOT NULL,
            outcome TEXT NOT NULL,
            count INTEGER NOT NULL,
            first_seen TEXT NOT NULL,
            last_seen TEXT NOT NULL,
            replacement_bytes INTEGER,
            PRIMARY KEY (session_id, path, expansion, mount, outcome),
            FOREIGN KEY (session_id) REFERENCES talon_sessions(session_id)
        );
        CREATE INDEX IF NOT EXISTS idx_talon_vfs_hashes
            ON talon_vfs_observations(dir_hash, file_hash);
        CREATE INDEX IF NOT EXISTS idx_talon_vfs_path
            ON talon_vfs_observations(path COLLATE BINARY);

        CREATE VIEW IF NOT EXISTS talon_vfs_totals AS
        SELECT
            path,
            directory,
            file,
            dir_hash,
            file_hash,
            expansion,
            mount,
            outcome,
            SUM(count) AS count,
            MIN(first_seen) AS first_seen,
            MAX(last_seen) AS last_seen,
            MAX(replacement_bytes) AS replacement_bytes,
            COUNT(DISTINCT session_id) AS sessions
        FROM talon_vfs_observations
        GROUP BY path, directory, file, dir_hash, file_hash, expansion, mount, outcome;
        """;
}
