using ChunkMaestro.Application;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class BackupMutationContractTests : IDisposable
{
    private static readonly string[] SyntheticSidecars = ["-wal", "-shm"];
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "chunkmaestro-backup-contract-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CancellationBetweenBatchesTakesPrecedenceOverNewSourceContention()
    {
        await using SqliteConnection source = await SeedAsync();
        string baseline = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        string[] before = Hashes(baseline);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        SqliteTransaction? contention = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalFleetStore.CreateBackupAsync(source, directory, cancellation.Token, remaining =>
            {
                // If cancellation is deferred until after Copy, the next real native
                // step hits SQLITE_BUSY instead. The large source still has work left.
                Assert.True(remaining > 0);
                cancellation.Cancel();
                contention = source.BeginTransaction();
            }));
        }
        finally
        {
            contention?.Dispose();
        }
        Assert.Equal(before, Hashes(baseline));
        Assert.Empty(Directory.GetFiles(directory, ".backup-*"));
        Assert.Equal(baseline, Assert.Single(Directory.GetFiles(directory, "before-migration-*.db")));
        await AssertRecoveryAsync(source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidateLogicalDamageAfterCopyIsRejectedAndFreshRequestRecovers(bool foreignKeyDamage)
    {
        await using SqliteConnection source = await SeedAsync();
        string baseline = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        string[] before = Hashes(baseline);
        string? damaged = null;
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.CreateBackupAsync(source, directory, default, remaining =>
        {
            if (remaining != 0)
            {
                return;
            }
            damaged = Assert.Single(Directory.GetFiles(directory, ".backup-*.partial"));
            // The native copy has committed. A second real SQLite connection now
            // damages only the disposable candidate, never the source or baseline.
            using SqliteConnection candidate = Connect(damaged);
            using SqliteCommand command = candidate.CreateCommand();
            command.CommandText = foreignKeyDamage
                ? "PRAGMA foreign_keys=OFF; INSERT INTO Hosts VALUES ('orphan','missing','synthetic-host','synthetic-address',0)"
                : "CREATE TABLE Damaged (Value INTEGER CHECK(Value>0)); PRAGMA ignore_check_constraints=ON; INSERT INTO Damaged VALUES (-1); PRAGMA ignore_check_constraints=OFF";
            command.ExecuteNonQuery();
            command.CommandText = foreignKeyDamage ? "PRAGMA foreign_key_check" : "PRAGMA quick_check";
            Assert.NotNull(command.ExecuteScalar());
            if (!foreignKeyDamage)
            {
                Assert.NotEqual("ok", command.ExecuteScalar());
            }
        }));
        Assert.NotNull(damaged);
        Assert.Equal(StorageFailure.CorruptDatabase, failure.Failure);
        Assert.DoesNotContain(directory, failure.ToString());
        Assert.Equal(before, Hashes(baseline));
        Assert.Empty(Directory.GetFiles(directory, ".backup-*"));
        Assert.Equal(baseline, Assert.Single(Directory.GetFiles(directory, "before-migration-*.db")));
        await AssertRecoveryAsync(source);
    }

    [Fact]
    public async Task CleanupRefusalDoesNotMaskCancellationOrPreventOtherSidecarCleanup()
    {
        await using SqliteConnection source = await SeedAsync();
        string baseline = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        string[] before = Hashes(baseline);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        string? refused = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalFleetStore.CreateBackupAsync(source, directory, cancellation.Token, remaining =>
        {
            if (remaining != 0)
            {
                return;
            }
            string candidate = Assert.Single(Directory.GetFiles(directory, ".backup-*.partial"));
            // SQLite has completed and removed its rollback journal. A directory
            // at that pathname causes a genuine File.Delete refusal on both OSes.
            refused = candidate + "-journal";
            Directory.CreateDirectory(refused);
            foreach (string suffix in SyntheticSidecars)
            {
                using FileStream sidecar = StorageLocation.CreateOrOpen(candidate + suffix);
                sidecar.WriteByte(42);
            }
            cancellation.Cancel();
        }));
        Assert.NotNull(refused);
        Assert.True(Directory.Exists(refused));
        Assert.Empty(Directory.GetFiles(directory, ".backup-*"));
        Assert.Equal(before, Hashes(baseline));
        Assert.Equal(baseline, Assert.Single(Directory.GetFiles(directory, "before-migration-*.db")));
        Directory.Delete(refused);
        await AssertRecoveryAsync(source);
    }

    private async Task<SqliteConnection> SeedAsync()
    {
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory)))
        {
        }
        foreach (string initial in Directory.GetFiles(directory, "before-migration-*.db"))
        {
            File.Delete(initial);
        }
        SqliteConnection source = Connect(Path.Combine(directory, "fleet.db"));
        using SqliteCommand command = source.CreateCommand();
        command.CommandText = "PRAGMA wal_autocheckpoint=0; WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<4096) INSERT INTO Fleets(Id,Name) SELECT printf('00000000-0000-0000-0000-%012d',x), printf('%0120d',x) FROM n";
        await command.ExecuteNonQueryAsync();
        return source;
    }

    private async Task AssertRecoveryAsync(SqliteConnection source)
    {
        string backup = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        string recovery = Path.Combine(directory, "recovery");
        StorageLocation.ForTests(recovery).Prepare();
        string database = Path.Combine(recovery, "fleet.db");
        using (StorageLocation.CreateOrOpen(database))
        {
        }
        File.Copy(backup, database, true);
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(recovery));
        Assert.Equal(4096, (await store.ReadAsync()).Fleets.Count);
    }

    private static SqliteConnection Connect(string path)
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private string[] Hashes(string baseline)
    {
        return new string[]
        {
            Path.Combine(directory, "fleet.db"), Path.Combine(directory, "fleet.db-wal"), baseline
        }.Select(path =>
        {
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }).ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
