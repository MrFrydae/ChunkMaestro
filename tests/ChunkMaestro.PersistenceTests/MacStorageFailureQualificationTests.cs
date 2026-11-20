using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Data;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

// These cases run on both supported platforms; macOS is the locally measured host.
public sealed class MacStorageFailureQualificationTests : IDisposable
{
    private const string Initial = "20261114034833_InitialFleet";
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "chunkmaestro-storage-failure-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CancellationDuringMigrationRollsBackTransactionRetainsBackupAndReleasesOwnership()
    {
        StorageLocation.ForTests(directory).Prepare();
        string database = Path.Combine(directory, "fleet.db");
        using (StorageLocation.CreateOrOpen(database))
        {
        }
        FleetId fleet = new FleetId(Guid.NewGuid());
        await using (SqliteConnection seed = Connect(database))
        {
            await seed.OpenAsync();
            await using FleetDbContext context = new FleetDbContext(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(seed).Options);
            await context.GetService<IMigrator>().MigrateAsync(Initial);
            context.Fleets.Add(new FleetRow
            {
                Id = fleet.Value,
                Name = "preserved-before-cancellation"
            });
            await context.SaveChangesAsync();
        }
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        bool ddlExecuted = false;
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalFleetStore.OpenCoreAsync(StorageLocation.ForTests(directory), async (context, token) =>
            {
                // Cancellation is controlled through the migration seam after real
                // transactional SQLite DDL. This is not an interrupted-process test.
                await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(token);
                await context.Database.ExecuteSqlRawAsync("CREATE TABLE CanceledMigration (Id INTEGER)", token);
                ddlExecuted = true;
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }, cancellation.Token));
        Assert.True(ddlExecuted);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        string backup = Assert.Single(Directory.GetFiles(directory, "before-migration-*.db"));
        byte[] backupBytes = await File.ReadAllBytesAsync(backup);
        Assert.Empty(Directory.GetFileSystemEntries(directory, ".backup-*"));
        await using (SqliteConnection original = Connect(database))
        {
            await original.OpenAsync();
            await using FleetDbContext context = new FleetDbContext(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(original).Options);
            Assert.Equal(new[]
            {
                Initial
            }, await context.Database.GetAppliedMigrationsAsync());
            Assert.Equal("preserved-before-cancellation", Assert.Single(await context.Fleets.ToArrayAsync()).Name);
            using SqliteCommand check = original.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='CanceledMigration'";
            Assert.Equal(0L, await check.ExecuteScalarAsync());
        }
        // Reviewed recovery is a separate copy, never an automatic replacement.
        string recovery = Path.Combine(directory, "reviewed-recovery");
        StorageLocation.ForTests(recovery).Prepare();
        string recoveredDatabase = Path.Combine(recovery, "fleet.db");
        using (StorageLocation.CreateOrOpen(recoveredDatabase))
        {
        }
        File.Copy(backup, recoveredDatabase, true);
        await using (LocalFleetStore recovered = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(recovery)))
        {
            Assert.Equal(new Fleet(fleet, "preserved-before-cancellation"), Assert.Single((await recovered.ReadAsync()).Fleets));
        }
        // Explicit retry on the original proves cancellation released its locks.
        await using (LocalFleetStore retry = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory)))
        {
            Assert.Equal(new Fleet(fleet, "preserved-before-cancellation"), Assert.Single((await retry.ReadAsync()).Fleets));
        }
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task DesignTimeContextsKeepModelOperationsInIndependentMemoryDatabases()
    {
        StorageLocation.ForTests(directory).Prepare();
        string sentinel = Path.Combine(directory, "fleet.db");
        using (FileStream file = StorageLocation.CreateOrOpen(sentinel))
        {
            file.WriteByte(73);
        }
        FleetDesignFactory factory = new FleetDesignFactory();
        await using FleetDbContext first = factory.CreateDbContext([sentinel]);
        await using FleetDbContext second = factory.CreateDbContext([]);
        Assert.Equal(ConnectionState.Closed, first.Database.GetDbConnection().State);
        Assert.Equal(ConnectionState.Closed, second.Database.GetDbConnection().State);
        Assert.False(first.Database.HasPendingModelChanges());
        await first.Database.OpenConnectionAsync();
        await second.Database.OpenConnectionAsync();
        await first.Database.EnsureCreatedAsync();
        await second.Database.EnsureCreatedAsync();
        first.Fleets.Add(new FleetRow
        {
            Id = Guid.NewGuid(),
            Name = "design-only"
        });
        await first.SaveChangesAsync();
        Assert.Single(await first.Fleets.ToArrayAsync());
        Assert.Empty(await second.Fleets.ToArrayAsync());
        Assert.Equal(new byte[]
        {
            73
        }, await File.ReadAllBytesAsync(sentinel));
        Assert.Equal(sentinel, Assert.Single(Directory.GetFileSystemEntries(directory)));
    }

    private static SqliteConnection Connect(string path)
    {
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
