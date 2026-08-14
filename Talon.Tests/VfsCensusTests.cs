using Microsoft.Data.Sqlite;
using Talon.Vfs;

namespace Talon.Tests;

public sealed class VfsCensusTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"talon-census-{Guid.NewGuid():N}");

    [Fact]
    public async Task WritesCompatiblePathsAndAggregatesResolutionOutcomes()
    {
        var databasePath = Path.Combine(root, "dat_db.db");
        using var census = new VfsCensus(databasePath, TimeSpan.FromMilliseconds(10));

        census.Record("common/data/example.etp", 0, 5, VfsResolutionOutcome.OriginalHit);
        census.Record("common/data/example.etp", 0, 5, VfsResolutionOutcome.OriginalHit);
        census.Record(
            "common/data/example.etp",
            1,
            15,
            VfsResolutionOutcome.OverrideHit,
            replacementBytes: 1234);
        await census.FlushAsync();
        census.Dispose();

        using var connection = OpenReadOnly(databasePath);
        using (var path = connection.CreateCommand())
        {
            path.CommandText =
                "SELECT file, directory, file_hash, dir_hash, blowfish_key FROM files";
            using var reader = path.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("example.etp", reader.GetString(0));
            Assert.Equal("common/data", reader.GetString(1));
            var expected = DqxPathHash.Describe("common/data/example.etp");
            Assert.Equal(expected.FileHash, reader.GetString(2));
            Assert.Equal(expected.DirectoryHash, reader.GetString(3));
            Assert.True(reader.IsDBNull(4));
            Assert.False(reader.Read());
        }

        using (var observations = connection.CreateCommand())
        {
            observations.CommandText = """
                SELECT outcome, count, replacement_bytes
                FROM talon_vfs_observations
                ORDER BY expansion, mount, outcome
                """;
            using var reader = observations.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("original-hit", reader.GetString(0));
            Assert.Equal(2, reader.GetInt64(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(reader.Read());
            Assert.Equal("override-hit", reader.GetString(0));
            Assert.Equal(1, reader.GetInt64(1));
            Assert.Equal(1234, reader.GetInt32(2));
            Assert.False(reader.Read());
        }

        Assert.Equal(3, ExecuteInt64(connection, "SELECT SUM(count) FROM talon_vfs_totals"));
        Assert.Equal(VfsCensus.SchemaVersion, ExecuteInt64(connection, "PRAGMA user_version"));
        using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", journal.ExecuteScalar());
    }

    [Fact]
    public async Task BackgroundWriterPublishesBeforeShutdown()
    {
        var databasePath = Path.Combine(root, "background.db");
        using var census = new VfsCensus(databasePath, TimeSpan.FromMilliseconds(10));
        census.Record("menu/example.rps", 0, 8, VfsResolutionOutcome.OriginalMiss);

        var observed = await WaitUntilAsync(() =>
        {
            if (!File.Exists(databasePath)) return false;
            try
            {
                using var connection = OpenReadOnly(databasePath);
                return ExecuteInt64(connection, "SELECT COUNT(*) FROM talon_vfs_observations") == 1;
            }
            catch (SqliteException) { return false; }
        });

        Assert.True(observed);
    }

    [Fact]
    public void AccumulatesPathsAndTotalsAcrossSessions()
    {
        var databasePath = Path.Combine(root, "persistent.db");
        using (var first = new VfsCensus(databasePath, TimeSpan.Zero))
            first.Record("menu/example.rps", 0, 8, VfsResolutionOutcome.OriginalHit);
        using (var second = new VfsCensus(databasePath, TimeSpan.Zero))
            second.Record("menu/example.rps", 0, 8, VfsResolutionOutcome.OriginalHit);

        using var connection = OpenReadOnly(databasePath);
        Assert.Equal(1, ExecuteInt64(connection, "SELECT COUNT(*) FROM files"));
        Assert.Equal(2, ExecuteInt64(connection, "SELECT COUNT(*) FROM talon_sessions"));
        Assert.Equal(2, ExecuteInt64(connection, "SELECT COUNT(*) FROM talon_vfs_observations"));
        Assert.Equal(2, ExecuteInt64(connection, "SELECT count FROM talon_vfs_totals"));
        Assert.Equal(2, ExecuteInt64(connection, "SELECT sessions FROM talon_vfs_totals"));
        Assert.Equal(
            0,
            ExecuteInt64(connection, "SELECT COUNT(*) FROM talon_sessions WHERE ended_at IS NULL"));
    }

    [Fact]
    public async Task KeepsObservedCaseButDeduplicatesCaseInsensitiveHashes()
    {
        var databasePath = Path.Combine(root, "case.db");
        using var census = new VfsCensus(databasePath, TimeSpan.FromMilliseconds(10));
        census.Record("Common/Data.bin", 0, 0, VfsResolutionOutcome.OriginalHit);
        census.Record("common/Data.bin", 0, 0, VfsResolutionOutcome.OriginalHit);
        await census.FlushAsync();
        census.Dispose();

        using var connection = OpenReadOnly(databasePath);
        Assert.Equal(1, ExecuteInt64(connection, "SELECT COUNT(*) FROM files"));
        Assert.Equal(2, ExecuteInt64(connection, "SELECT COUNT(*) FROM talon_vfs_observations"));
    }

    [Fact]
    public async Task ConcurrentSessionsShareOneCatalog()
    {
        var databasePath = Path.Combine(root, "concurrent.db");
        using var first = new VfsCensus(databasePath, TimeSpan.FromMilliseconds(10));
        using var second = new VfsCensus(databasePath, TimeSpan.FromMilliseconds(10));
        first.Record("menu/first.rps", 0, 0, VfsResolutionOutcome.OriginalHit);
        second.Record("menu/second.rps", 0, 0, VfsResolutionOutcome.OriginalHit);

        await Task.WhenAll(first.FlushAsync(), second.FlushAsync());
        first.Dispose();
        second.Dispose();

        using var connection = OpenReadOnly(databasePath);
        Assert.Equal(2, ExecuteInt64(connection, "SELECT COUNT(*) FROM files"));
        Assert.Equal(2, ExecuteInt64(connection, "SELECT COUNT(*) FROM talon_sessions"));
        Assert.Equal(2, ExecuteInt64(connection, "SELECT COUNT(*) FROM talon_vfs_observations"));
    }

    [Fact]
    public async Task PreservesExistingCatalogPathAndBlowfishKey()
    {
        var databasePath = Path.Combine(root, "existing.db");
        Directory.CreateDirectory(root);
        using (var connection = OpenWritable(databasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE files (
                    file TEXT,
                    directory TEXT,
                    file_hash TEXT,
                    dir_hash TEXT,
                    blowfish_key TEXT);
                CREATE UNIQUE INDEX idx_files_hashes ON files(dir_hash, file_hash);
                INSERT INTO files VALUES (
                    'Example.etp', 'Common/Data', '87b86ab7', '1b46f4c2', 'known-key');
                """;
            command.ExecuteNonQuery();
        }

        using var census = new VfsCensus(databasePath, TimeSpan.FromMilliseconds(10));
        census.Record("common/data/example.etp", 0, 0, VfsResolutionOutcome.OriginalHit);
        await census.FlushAsync();
        census.Dispose();

        using var result = OpenReadOnly(databasePath);
        using var query = result.CreateCommand();
        query.CommandText = "SELECT file, directory, blowfish_key FROM files";
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Example.etp", reader.GetString(0));
        Assert.Equal("Common/Data", reader.GetString(1));
        Assert.Equal("known-key", reader.GetString(2));
        Assert.False(reader.Read());
    }

    [Fact]
    public void UsesSharedLocalApplicationDataCatalog()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galapa",
            "DQX",
            "dat_db.db");

        Assert.Equal(expected, VfsCensus.GetDefaultOutputPath());
    }

    [Theory]
    [InlineData("menu/rps/menu/Login/client_dds.rps")]
    [InlineData("MENU/RPS/MENU/LOGIN/CLIENT_DDS.RPS")]
    [InlineData("menu\\rps\\menu\\Login\\client_dds.rps")]
    public void PathHashMatchesKnownArchiveHash(string path)
    {
        Assert.Equal(0x159B070993EBE61AUL, DqxPathHash.Compute(path));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenWritable(string path)
    {
        SQLitePCL.Batteries_V2.Init();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long ExecuteInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(10);
        }
        return false;
    }
}
