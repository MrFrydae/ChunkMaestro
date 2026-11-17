using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class PersistedIdentityTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "chunkmaestro-persisted-identity-" + Guid.NewGuid().ToString("N"));
    private readonly FleetId fleet = new FleetId(Guid.NewGuid());
    private readonly HostId host = new HostId(Guid.NewGuid());
    private readonly InstanceId instance = new InstanceId(Guid.NewGuid());
    private StorageLocation Location => StorageLocation.ForTests(directory);
    private string DatabasePath => Path.Combine(directory, "fleet.db");

    [Theory]
    [InlineData("Fleets", "text")]
    [InlineData("Hosts", "text")]
    [InlineData("Instances", "text")]
    [InlineData("Fleets", "empty")]
    [InlineData("Hosts", "empty")]
    [InlineData("Instances", "empty")]
    [InlineData("Fleets", "integer")]
    [InlineData("Hosts", "integer")]
    [InlineData("Instances", "integer")]
    [InlineData("Fleets", "blob")]
    [InlineData("Hosts", "blob")]
    [InlineData("Instances", "blob")]
    public async Task DamagedIdentityIsRefusedWithValidForeignKeysAndPreservedForExplicitRecovery(string table, string damage)
    {
        await SeedAsync();
        await using (SqliteConnection connection = await ConnectAsync())
        {
            using SqliteCommand command = connection.CreateCommand();
            // Remove dependents so the malformed primary identity has no broken
            // reference: the existing FK check alone cannot detect this damage.
            command.CommandText = table switch
            {
                "Fleets" => "DELETE FROM Endpoints; DELETE FROM Instances; DELETE FROM Hosts",
                "Hosts" => "DELETE FROM Endpoints; DELETE FROM Instances",
                _ => "DELETE FROM Endpoints"
            };
            await command.ExecuteNonQueryAsync();
        }
        object invalid = damage switch
        {
            "empty" => Guid.Empty,
            "integer" => 42L,
            "blob" => new byte[]
            {
                1, 2, 3
            },
            _ => "private-not-a-guid"
        };
        await SetAsync(table, invalid);
        byte[] original = SHA256.HashData(await File.ReadAllBytesAsync(DatabasePath));
        string raw = await RawAsync(table);
        string[] backups = Directory.GetFiles(directory, "before-migration-*.db").Order().ToArray();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            LocalFleetStore? unexpected = null;
            FleetStorageException failure;
            try
            {
                failure = await Assert.ThrowsAsync<FleetStorageException>(async () =>
                {
                    unexpected = await LocalFleetStore.OpenAsync(Location);
                });
            }
            finally
            {
                if (unexpected is not null)
                {
                    await unexpected.DisposeAsync();
                }
            }
            Assert.Equal(StorageFailure.CorruptDatabase, failure.Failure);
            Assert.Contains("recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-", failure.ToString());
            Assert.DoesNotContain(directory, failure.ToString());
            Assert.Null(failure.InnerException);
            Assert.Null(failure.BackupPath);
            Assert.Equal(original, SHA256.HashData(await File.ReadAllBytesAsync(DatabasePath)));
            Assert.Equal(raw, await RawAsync(table));
            Assert.Equal(backups, Directory.GetFiles(directory, "before-migration-*.db").Order().ToArray());
        }
        Guid restoredId = table == "Fleets" ? fleet.Value : table == "Hosts" ? host.Value : instance.Value;
        await SetAsync(table, restoredId);
        await using LocalFleetStore recovered = await LocalFleetStore.OpenAsync(Location);
        FleetInventory inventory = await recovered.ReadAsync();
        Assert.Equal(fleet, Assert.Single(inventory.Fleets).Id);
        if (table != "Fleets")
        {
            Assert.Equal(host, Assert.Single(inventory.Hosts).Id);
        }
        if (table == "Instances")
        {
            Assert.Equal(instance, Assert.Single(inventory.Instances).Id);
        }
        Assert.Equal(backups, Directory.GetFiles(directory, "before-migration-*.db").Order().ToArray());
    }

    private async Task SeedAsync()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        await store.RegisterFleetAsync(fleet, "Identity fleet");
        await store.RegisterHostAsync(host, fleet, "Identity host", "identity.example");
        await store.RegisterInstanceAsync(instance, host, "Identity instance", "minecraft.service",
            [new Endpoint(EndpointEdition.Java, "identity.example", 25565)]);
    }

    private async Task<SqliteConnection> ConnectAsync()
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private async Task SetAsync(string table, object value)
    {
        await using SqliteConnection connection = await ConnectAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = table switch
        {
            "Fleets" => "UPDATE Fleets SET Id=$value",
            "Hosts" => "UPDATE Hosts SET Id=$value",
            _ => "UPDATE Instances SET Id=$value"
        };
        command.Parameters.AddWithValue("$value", value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<string> RawAsync(string table)
    {
        await using SqliteConnection connection = await ConnectAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            Assert.False(await reader.ReadAsync());
        }
        command.CommandText = table switch
        {
            "Fleets" => "SELECT typeof(Id)||':'||quote(Id) FROM Fleets",
            "Hosts" => "SELECT typeof(Id)||':'||quote(Id) FROM Hosts",
            _ => "SELECT typeof(Id)||':'||quote(Id) FROM Instances"
        };
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
