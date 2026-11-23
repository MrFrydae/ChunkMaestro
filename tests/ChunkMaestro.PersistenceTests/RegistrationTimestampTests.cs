using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class RegistrationTimestampTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "chunkmaestro-registration-time-" + Guid.NewGuid().ToString("N"));
    private readonly FleetId fleet = new FleetId(Guid.NewGuid());
    private readonly HostId host = new HostId(Guid.NewGuid());
    private readonly InstanceId instance = new InstanceId(Guid.NewGuid());
    private StorageLocation Location => StorageLocation.ForTests(directory);
    private string DatabasePath => Path.Combine(directory, "fleet.db");

    [Theory]
    [InlineData("Hosts", long.MaxValue)]
    [InlineData("Instances", long.MaxValue)]
    [InlineData("Hosts", long.MinValue)]
    [InlineData("Instances", long.MinValue)]
    [InlineData("Hosts", -62135596800001L)]
    [InlineData("Instances", -62135596800001L)]
    [InlineData("Hosts", 253402300800000L)]
    [InlineData("Instances", 253402300800000L)]
    [InlineData("Hosts", "private-invalid-instant")]
    [InlineData("Instances", "private-invalid-instant")]
    [InlineData("Hosts", 0.5)]
    [InlineData("Instances", 0.5)]
    public async Task InvalidPersistedRegistrationTimestampIsRefusedWithoutRepair(string table, object timestamp)
    {
        await SeedAsync();
        await SetAsync(table, timestamp);
        string originalRow = await ReadRawAsync(table);
        byte[] originalDatabase = SHA256.HashData(await File.ReadAllBytesAsync(DatabasePath));
        string[] originalBackups = Backups();
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
            Assert.Contains("preserve", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(directory, failure.ToString());
            Assert.DoesNotContain("private-", failure.ToString());
            Assert.DoesNotContain("SELECT", failure.ToString());
            Assert.Null(failure.InnerException);
            Assert.Null(failure.BackupPath);
            Assert.Equal(originalDatabase, SHA256.HashData(await File.ReadAllBytesAsync(DatabasePath)));
            Assert.Equal(originalRow, await ReadRawAsync(table));
            Assert.Equal(originalBackups, Backups());
        }
        // Only this explicit fixture recovery changes the rejected value.
        await SetAsync(table, 0L);
        await using LocalFleetStore recovered = await LocalFleetStore.OpenAsync(Location);
        FleetInventory inventory = await recovered.ReadAsync();
        Assert.Equal(fleet, Assert.Single(inventory.Fleets).Id);
        Host savedHost = Assert.Single(inventory.Hosts);
        Instance savedInstance = Assert.Single(inventory.Instances);
        Assert.Equal(host, savedHost.Id);
        Assert.Equal(instance, savedInstance.Id);
        Assert.Equal(DateTimeOffset.UnixEpoch, table == "Hosts" ? savedHost.RegisteredAt : savedInstance.RegisteredAt);
        Assert.Single(savedInstance.Endpoints);
        Assert.Equal(originalBackups, Backups());
    }

    [Theory]
    [InlineData(-62135596800000L, 253402300799999L)]
    [InlineData(253402300799999L, -62135596800000L)]
    [InlineData(0L, 0L)]
    public async Task SupportedRegistrationTimestampBoundariesRemainReadable(long hostTime, long instanceTime)
    {
        await SeedAsync();
        await SetAsync("Hosts", hostTime);
        await SetAsync("Instances", instanceTime);
        string[] originalBackups = Backups();
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        FleetInventory inventory = await reopened.ReadAsync();
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(hostTime), Assert.Single(inventory.Hosts).RegisteredAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(instanceTime), Assert.Single(inventory.Instances).RegisteredAt);
        Assert.Equal(originalBackups, Backups());
    }

    private async Task SeedAsync()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        await store.RegisterFleetAsync(fleet, "Timestamp fixture fleet");
        await store.RegisterHostAsync(host, fleet, "Timestamp fixture host", "timestamp.example");
        await store.RegisterInstanceAsync(instance, host, "Timestamp fixture instance", "minecraft.service",
            [new Endpoint(EndpointEdition.Java, "timestamp.example", 25565)]);
    }

    private async Task<SqliteConnection> ConnectAsync()
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private async Task SetAsync(string table, object timestamp)
    {
        await using SqliteConnection connection = await ConnectAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = table == "Hosts" ? "UPDATE Hosts SET RegisteredAt=$value" : "UPDATE Instances SET RegisteredAt=$value";
        command.Parameters.AddWithValue("$value", timestamp);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<string> ReadRawAsync(string table)
    {
        await using SqliteConnection connection = await ConnectAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = table == "Hosts" ? "SELECT typeof(RegisteredAt)||':'||quote(RegisteredAt) FROM Hosts" :
            "SELECT typeof(RegisteredAt)||':'||quote(RegisteredAt) FROM Instances";
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private string[] Backups()
    {
        return Directory.GetFiles(directory, "before-migration-*.db").Order().ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
