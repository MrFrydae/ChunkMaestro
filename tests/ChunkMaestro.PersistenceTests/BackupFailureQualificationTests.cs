using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class BackupFailureQualificationTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "chunkmaestro-backup-failure-" + Guid.NewGuid().ToString("N"));
    private readonly FleetId fleet = new FleetId(Guid.NewGuid());

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ObserverOrVerifierFailurePreservesWalAndCompletedBackupAndAllowsExplicitRecovery(bool verifierFailure, bool accessFailure)
    {
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory)))
        {
        }
        foreach (string initial in Directory.GetFiles(directory, "before-migration-*.db"))
        {
            File.Delete(initial);
        }
        await using SqliteConnection source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "fleet.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await source.OpenAsync();
        using (SqliteCommand seed = source.CreateCommand())
        {
            seed.CommandText = "PRAGMA wal_autocheckpoint=0; INSERT INTO Fleets(Id,Name) VALUES ($id,'committed-private-value')";
            seed.Parameters.AddWithValue("$id", fleet.Value.ToString().ToUpperInvariant());
            await seed.ExecuteNonQueryAsync();
        }
        Assert.True(new FileInfo(Path.Combine(directory, "fleet.db-wal")).Length > 0);
        string prior = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        string[] before = Hashes(prior);
        int observations = 0;
        bool verified = false;
        // Only fault delivery uses existing seams. The native copy and all cleanup
        // are real; these exceptions are not native or full-volume fault evidence.
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() =>
            IncrementalBackupDriver.CreateAsync(source, directory, async (candidate, token) =>
            {
                using SqliteCommand check = candidate.CreateCommand();
                check.CommandText = "PRAGMA quick_check";
                Assert.Equal("ok", await check.ExecuteScalarAsync(token));
                check.CommandText = "SELECT Name FROM Fleets";
                Assert.Equal("committed-private-value", await check.ExecuteScalarAsync(token));
                verified = true;
                ThrowPrivateFailure(accessFailure);
            }, remaining =>
            {
                observations++;
                Assert.Single(Directory.GetFiles(directory, ".backup-*.partial"));
                if (!verifierFailure)
                {
                    ThrowPrivateFailure(accessFailure);
                }
            }, default));
        Assert.True(observations > 0);
        Assert.Equal(verifierFailure, verified);
        Assert.Equal(StorageFailure.StorageUnavailable, failure.Failure);
        Assert.Null(failure.BackupPath);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(directory, failure.ToString());
        Assert.DoesNotContain("committed-private-value", failure.ToString());
        Assert.Contains("preserved", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Hashes(prior));
        Assert.Empty(Directory.GetFileSystemEntries(directory, ".backup-*"));
        Assert.Equal(prior, Assert.Single(Directory.GetFiles(directory, "before-migration-*.db")));
        // A new public request must work: the failed observer cannot retain a
        // native backup operation or leave the source transaction unusable.
        string fresh = await LocalFleetStore.CreateBackupAsync(source, directory, default);
        await RecoverAsync(prior, "prior-recovery");
        await RecoverAsync(fresh, "fresh-recovery");
        Assert.Equal(before, Hashes(prior));
    }

    private void ThrowPrivateFailure(bool accessFailure)
    {
        string diagnostic = directory + ": committed-private-value";
        if (accessFailure)
        {
            throw new UnauthorizedAccessException(diagnostic);
        }
        throw new IOException(diagnostic);
    }

    private async Task RecoverAsync(string backup, string name)
    {
        string recovery = Path.Combine(directory, name);
        StorageLocation.ForTests(recovery).Prepare();
        string database = Path.Combine(recovery, "fleet.db");
        using (StorageLocation.CreateOrOpen(database))
        {
        }
        File.Copy(backup, database, true);
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(recovery));
        FleetInventory inventory = await store.ReadAsync();
        Assert.Equal(new Fleet(fleet, "committed-private-value"), Assert.Single(inventory.Fleets));
        Assert.Empty(inventory.Hosts);
        Assert.Empty(inventory.Instances);
    }

    private string[] Hashes(string prior)
    {
        return new[]
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
