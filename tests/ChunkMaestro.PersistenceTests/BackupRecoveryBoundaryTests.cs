using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class BackupRecoveryBoundaryTests : IDisposable
{
    private static readonly Guid CommittedId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "chunkmaestro-backup-boundary-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UncommittedSourceWriteRefusesBackupWithoutPublishingAndExplicitRollbackRecovers()
    {
        await using SqliteConnection source = await SeedAsync();
        string prior = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        string[] before = Hashes(prior);
        using (SqliteTransaction transaction = source.BeginTransaction())
        {
            using SqliteCommand write = source.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = "UPDATE Fleets SET Name='uncommitted-private-name'";
            Assert.Equal(1, await write.ExecuteNonQueryAsync());

            FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() =>
                LocalFleetStore.CreateBackupAsync(source, directory, default));
            Assert.Equal(StorageFailure.StorageUnavailable, failure.Failure);
            Assert.Null(failure.BackupPath);
            Assert.DoesNotContain(directory, failure.ToString());
            Assert.DoesNotContain("uncommitted-private-name", failure.ToString());
            AssertUnpublished(prior, before);
            // Failure must not roll back the caller's work or silently commit it.
            write.CommandText = "SELECT Name FROM Fleets";
            Assert.Equal("uncommitted-private-name", await write.ExecuteScalarAsync());
            transaction.Rollback();
        }

        AssertUnpublished(prior, before);
        await AssertRecoveryAsync(prior, "prior-recovery");
        string fresh = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        await AssertRecoveryAsync(fresh, "fresh-recovery");
        Assert.Equal(before, Hashes(prior));
        await source.DisposeAsync();
        await AssertStoreAsync(directory);
    }

    [Fact]
    public async Task CancellationAfterRealVerificationPreventsPublicationAndFreshPublicBackupRecovers()
    {
        await using SqliteConnection source = await SeedAsync();
        string prior = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        string[] before = Hashes(prior);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        bool verified = false;

        // The existing driver verifier seam controls this precise boundary only.
        // Copying, SQLite checks, file flushing and cleanup still execute normally.
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IncrementalBackupDriver.CreateAsync(source, directory, async (candidate, token) =>
            {
                using SqliteCommand check = candidate.CreateCommand();
                check.CommandText = "PRAGMA quick_check";
                Assert.Equal("ok", await check.ExecuteScalarAsync(token));
                check.CommandText = "PRAGMA foreign_key_check";
                using (SqliteDataReader references = await check.ExecuteReaderAsync(token))
                {
                    Assert.False(await references.ReadAsync(token));
                }
                check.CommandText = "SELECT Name FROM Fleets";
                Assert.Equal("committed-in-wal", await check.ExecuteScalarAsync(token));
                Assert.Single(Directory.GetFiles(directory, ".backup-*.partial"));
                Assert.Equal(prior, Assert.Single(Directory.GetFiles(directory, "before-migration-*.db")));
                verified = true;
                cancellation.Cancel();
            }, null, cancellation.Token));

        Assert.True(verified);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        AssertUnpublished(prior, before);
        await AssertRecoveryAsync(prior, "prior-recovery");
        string fresh = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        await AssertRecoveryAsync(fresh, "fresh-recovery");
        Assert.Equal(before, Hashes(prior));
        await source.DisposeAsync();
        await AssertStoreAsync(directory);
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
        SqliteConnection source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "fleet.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await source.OpenAsync();
        using SqliteCommand seed = source.CreateCommand();
        seed.CommandText = "PRAGMA wal_autocheckpoint=0; INSERT INTO Fleets(Id,Name) VALUES ($id,'committed-in-wal')";
        seed.Parameters.AddWithValue("$id", CommittedId.ToString().ToUpperInvariant());
        await seed.ExecuteNonQueryAsync();
        Assert.True(new FileInfo(Path.Combine(directory, "fleet.db-wal")).Length > 0);
        return source;
    }

    private void AssertUnpublished(string prior, string[] before)
    {
        Assert.Equal(before, Hashes(prior));
        Assert.Empty(Directory.GetFileSystemEntries(directory, ".backup-*"));
        Assert.Equal(prior, Assert.Single(Directory.GetFiles(directory, "before-migration-*.db")));
    }

    private async Task AssertRecoveryAsync(string backup, string name)
    {
        byte[] before = await File.ReadAllBytesAsync(backup);
        string recovery = Path.Combine(directory, name);
        StorageLocation.ForTests(recovery).Prepare();
        string database = Path.Combine(recovery, "fleet.db");
        using (StorageLocation.CreateOrOpen(database))
        {
        }
        File.Copy(backup, database, true);
        await AssertStoreAsync(recovery);
        Assert.Equal(before, await File.ReadAllBytesAsync(backup));
    }

    private static async Task AssertStoreAsync(string path)
    {
        await using LocalFleetStore recovered = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(path));
        FleetInventory inventory = await recovered.ReadAsync();
        Assert.Equal(new Fleet(new FleetId(CommittedId), "committed-in-wal"), Assert.Single(inventory.Fleets));
        Assert.Empty(inventory.Hosts);
        Assert.Empty(inventory.Instances);
    }

    private string[] Hashes(string prior)
    {
        return new string[]
        {
            Path.Combine(directory, "fleet.db"), Path.Combine(directory, "fleet.db-wal"), prior
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
