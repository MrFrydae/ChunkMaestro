using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class StoreFailureMutationContractTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "chunkmaestro-store-failure-" + Guid.NewGuid().ToString("N"));
    private readonly FleetId fleet = new FleetId(Guid.NewGuid());
    private StorageLocation Location => StorageLocation.ForTests(directory);
    private string DatabasePath => Path.Combine(directory, "fleet.db");

    [Fact]
    public async Task MissingInitialHistoryEntryIsRefusedBeforeBackupAndExplicitRecoveryReleasesOwnership()
    {
        await SeedAsync();
        string initial;
        string productVersion;
        await using (SqliteConnection connection = await ConnectAsync())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId LIMIT 1";
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                initial = reader.GetString(0);
                productVersion = reader.GetString(1);
            }
            command.CommandText = "DELETE FROM __EFMigrationsHistory WHERE MigrationId=$id";
            command.Parameters.AddWithValue("$id", initial);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await AssertPreservedRefusalAsync(StorageFailure.UnsupportedSchema);
        await using (SqliteConnection connection = await ConnectAsync())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO __EFMigrationsHistory VALUES ($id, $version)";
            command.Parameters.AddWithValue("$id", initial);
            command.Parameters.AddWithValue("$version", productVersion);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await AssertRecoveredAsync();
    }

    [Theory]
    [InlineData("ALTER TABLE __EFMigrationsHistory RENAME COLUMN MigrationId TO HiddenMigrationId",
        "ALTER TABLE __EFMigrationsHistory RENAME COLUMN HiddenMigrationId TO MigrationId")]
    [InlineData("CREATE VIEW SyntheticInventoryView AS SELECT Name FROM Fleets", "DROP VIEW SyntheticInventoryView")]
    public async Task UnsupportedMetadataReturnsSafeGuidanceAndLeavesRecoveryToCaller(string damage, string recovery)
    {
        await SeedAsync();
        await using (SqliteConnection connection = await ConnectAsync())
        {
            await ExecuteAsync(connection, damage);
        }
        await AssertPreservedRefusalAsync(StorageFailure.UnsupportedSchema);
        await using (SqliteConnection connection = await ConnectAsync())
        {
            // The fixture performs explicit repair only after proving startup preserved it.
            await ExecuteAsync(connection, recovery);
        }
        await AssertRecoveredAsync();
    }

    [Fact]
    public async Task HighestSupportedUserVersionReopensPopulatedInventoryWithoutAnotherMigrationBackup()
    {
        await SeedAsync();
        string[] backups = Backups();
        await using (SqliteConnection connection = await ConnectAsync())
        {
            await using FleetDbContext context = new FleetDbContext(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(connection).Options);
            int supported = context.Database.GetMigrations().Count();
            await ExecuteAsync(connection, $"PRAGMA user_version={supported}");
        }
        await AssertRecoveredAsync();
        Assert.Equal(backups, Backups());
    }

    [Fact]
    public async Task ExclusiveStartupLockReturnsAvailabilityGuidanceAndRetryPreservesInventory()
    {
        await SeedAsync();
        string[] backups = Backups();
        await using (SqliteConnection writer = await ConnectAsync())
        {
            // DELETE mode makes BEGIN EXCLUSIVE deny startup reads as well as writes.
            await ExecuteAsync(writer, "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE");
            try
            {
                FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
                Assert.Equal(StorageFailure.StorageUnavailable, failure.Failure);
                AssertGuidance(failure);
                Assert.Null(failure.BackupPath);
            }
            finally
            {
                await ExecuteAsync(writer, "ROLLBACK");
            }
        }
        Assert.Equal(backups, Backups());
        await AssertRecoveredAsync();
    }

    [Fact]
    public async Task OperationalEndpointFailureRollsBackInstanceAndAllowsExplicitRetryWithoutReplay()
    {
        await SeedAsync();
        HostId host = new HostId(Guid.NewGuid());
        InstanceId instance = new InstanceId(Guid.NewGuid());
        Endpoint[] endpoints = [new(EndpointEdition.Java, "synthetic.example", 25565), new(EndpointEdition.Bedrock, "synthetic.example", 19132)];
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
            await store.RegisterHostAsync(host, fleet, "Synthetic host", "synthetic.example");
            await using SqliteConnection connection = await ConnectAsync();
            // Genuine SQLite execution failure after the instance insert, inside SaveChanges.
            // The nonexistent target is synthetic and is never repaired by the store.
            await ExecuteAsync(connection, "CREATE TRIGGER SyntheticFailure AFTER INSERT ON Endpoints BEGIN INSERT INTO MissingSyntheticTarget VALUES (NEW.Port); END");
            try
            {
                FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => store.RegisterInstanceAsync(
                    instance, host, "private-synthetic-input", "synthetic.service", endpoints));
                Assert.Equal(StorageFailure.StorageUnavailable, failure.Failure);
                AssertGuidance(failure);
                Assert.DoesNotContain("private-synthetic-input", failure.ToString());
                Assert.DoesNotContain("MissingSyntheticTarget", failure.ToString());
                Assert.Empty((await store.ReadAsync()).Instances);
                using SqliteCommand rows = connection.CreateCommand();
                rows.CommandText = "SELECT COUNT(*) FROM Endpoints";
                Assert.Equal(0L, await rows.ExecuteScalarAsync());
            }
            finally
            {
                await ExecuteAsync(connection, "DROP TRIGGER SyntheticFailure");
            }
            // Resolving availability must not replay the ambiguous operation automatically.
            Assert.Empty((await store.ReadAsync()).Instances);
            await store.RegisterInstanceAsync(instance, host, "Reviewed retry", "synthetic.service", endpoints);
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        Instance saved = Assert.Single((await reopened.ReadAsync()).Instances);
        Assert.Equal(instance, saved.Id);
        Assert.Equal("Reviewed retry", saved.Name);
        Assert.Equal(endpoints, saved.Endpoints);
    }

    private async Task SeedAsync()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        await store.RegisterFleetAsync(fleet, "Preserved synthetic inventory");
    }

    private async Task AssertRecoveredAsync()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        Fleet saved = Assert.Single((await store.ReadAsync()).Fleets);
        Assert.Equal(fleet, saved.Id);
        Assert.Equal("Preserved synthetic inventory", saved.Name);
    }

    private async Task AssertPreservedRefusalAsync(StorageFailure category)
    {
        byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(DatabasePath));
        string[] backups = Backups();
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
        Assert.Equal(category, failure.Failure);
        AssertGuidance(failure);
        Assert.Null(failure.BackupPath);
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(DatabasePath)));
        Assert.Equal(backups, Backups());
    }

    private void AssertGuidance(FleetStorageException failure)
    {
        // Operational guidance must be present and sanitized; its wording is not an API.
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
        Assert.DoesNotContain(directory, failure.ToString());
        Assert.DoesNotContain("SQLite Error", failure.ToString());
        Assert.Null(failure.InnerException);
    }

    private string[] Backups()
    {
        return Directory.GetFiles(directory, "before-migration-*.db").Order().ToArray();
    }

    private async Task<SqliteConnection> ConnectAsync()
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
