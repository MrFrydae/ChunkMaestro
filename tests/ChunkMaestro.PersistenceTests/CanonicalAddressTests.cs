using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class CanonicalAddressTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "cm-canonical-address-" + Guid.NewGuid().ToString("N"));
    private readonly FleetId fleet = new FleetId(Guid.NewGuid());
    private readonly HostId host = new HostId(Guid.NewGuid());
    private StorageLocation Location => StorageLocation.ForTests(directory);
    private string DatabasePath => Path.Combine(directory, "fleet.db");

    [Theory]
    [InlineData("Hosts", "EXAMPLE.COM.")]
    [InlineData("Hosts", "127.1")]
    [InlineData("Hosts", "2001:0DB8:0:0:0:0:0:1")]
    [InlineData("Hosts", " private.example")]
    [InlineData("Hosts", "private_invalid.example")]
    [InlineData("Hosts", "")]
    [InlineData("Endpoints", "EXAMPLE.COM.")]
    [InlineData("Endpoints", "127.1")]
    [InlineData("Endpoints", "2001:0DB8:0:0:0:0:0:1")]
    [InlineData("Endpoints", " private.example")]
    [InlineData("Endpoints", "private_invalid.example")]
    [InlineData("Endpoints", "")]
    public async Task DamagedAddressIsRefusedWithoutRepairAndExplicitCopyCanRecover(string table, string address)
    {
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
            await store.RegisterFleetAsync(fleet, "Fixture");
            await store.RegisterHostAsync(host, fleet, "Fixture", "Example.COM.");
            await store.RegisterInstanceAsync(new InstanceId(Guid.NewGuid()), host, "Fixture", "fixture.service",
                [new Endpoint(EndpointEdition.Java, "2001:0DB8:0:0:0:0:0:1", 25565)]);
        }
        await SetAddressAsync(DatabasePath, table, address);
        string backup = Path.Combine(directory, "before-migration-existing.db");
        await CopySecurelyAsync(DatabasePath, backup);
        byte[] original = SHA256.HashData(await File.ReadAllBytesAsync(DatabasePath));
        byte[] priorBackup = SHA256.HashData(await File.ReadAllBytesAsync(backup));
        string[] backups = Backups();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => OpenAndDisposeAsync(Location));
            Assert.Equal(StorageFailure.CorruptDatabase, failure.Failure);
            Assert.Contains("preserve", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("recovery", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(directory, failure.ToString());
            Assert.DoesNotContain("private", failure.ToString());
            Assert.Null(failure.InnerException);
            Assert.Null(failure.BackupPath);
            Assert.Equal(original, SHA256.HashData(await File.ReadAllBytesAsync(DatabasePath)));
            Assert.Equal(priorBackup, SHA256.HashData(await File.ReadAllBytesAsync(backup)));
            Assert.Equal(backups, Backups());
        }

        // Only explicit fixture recovery changes a separate copy of the rejected data.
        string recoveredDirectory = Path.Combine(directory, "reviewed-copy");
        StorageLocation recoveredLocation = StorageLocation.ForTests(recoveredDirectory);
        recoveredLocation.Prepare();
        string recoveredDatabase = Path.Combine(recoveredDirectory, "fleet.db");
        await CopySecurelyAsync(backup, recoveredDatabase);
        await SetAddressAsync(recoveredDatabase, table, table == "Hosts" ? "example.com" : "2001:db8::1");
        await using (LocalFleetStore recovered = await LocalFleetStore.OpenAsync(recoveredLocation))
        {
            FleetInventory inventory = await recovered.ReadAsync();
            Assert.Equal("example.com", Assert.Single(inventory.Hosts).Address);
            Assert.Equal("2001:db8::1", Assert.Single(Assert.Single(inventory.Instances).Endpoints).Address);
            FleetStorageException duplicate = await Assert.ThrowsAsync<FleetStorageException>(() => recovered.RegisterHostAsync(
                new HostId(Guid.NewGuid()), fleet, "Duplicate", "EXAMPLE.COM."));
            Assert.Equal(StorageFailure.InvalidRegistration, duplicate.Failure);
            Assert.Single((await recovered.ReadAsync()).Hosts);
        }
        Assert.Equal(original, SHA256.HashData(await File.ReadAllBytesAsync(DatabasePath)));
        Assert.Equal(priorBackup, SHA256.HashData(await File.ReadAllBytesAsync(backup)));
    }

    [Theory]
    [InlineData("Example.COM.", "example.com")]
    [InlineData("127.1", "127.0.0.1")]
    [InlineData("2001:0DB8:0:0:0:0:0:1", "2001:db8::1")]
    public async Task PublicRegistrationNormalizesAddressesAndReopenRetainsUniqueness(string input, string expected)
    {
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
            await store.RegisterFleetAsync(fleet, "Fixture");
            await store.RegisterHostAsync(host, fleet, "Fixture", input);
            await store.RegisterInstanceAsync(new InstanceId(Guid.NewGuid()), host, "Fixture", "fixture.service",
                [new Endpoint(EndpointEdition.Java, input, 25565)]);
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        FleetInventory inventory = await reopened.ReadAsync();
        Assert.Equal(expected, Assert.Single(inventory.Hosts).Address);
        Assert.Equal(expected, Assert.Single(Assert.Single(inventory.Instances).Endpoints).Address);
        FleetStorageException duplicate = await Assert.ThrowsAsync<FleetStorageException>(() => reopened.RegisterHostAsync(
            new HostId(Guid.NewGuid()), fleet, "Duplicate", expected));
        Assert.Equal(StorageFailure.InvalidRegistration, duplicate.Failure);
        Assert.Single((await reopened.ReadAsync()).Hosts);
    }

    [Theory]
    [InlineData("20261114034833_InitialFleet")]
    [InlineData("20261115020704_EnforceEndpointInvariants")]
    public async Task HistoricalNoncanonicalHostIsRefusedBeforeUpgradeOrBackup(string version)
    {
        Location.Prepare();
        using (StorageLocation.CreateOrOpen(DatabasePath))
        {
        }
        await using (SqliteConnection connection = await ConnectAsync(DatabasePath))
        {
            await using FleetDbContext context = new FleetDbContext(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(connection).Options);
            await context.GetService<IMigrator>().MigrateAsync(version);
            context.Fleets.Add(new FleetRow
            {
                Id = fleet.Value,
                Name = "Fixture"
            });
            context.Hosts.Add(new HostRow
            {
                Id = host.Value,
                FleetId = fleet.Value,
                Name = "Fixture",
                Address = "EXAMPLE.COM.",
                RegisteredAt = 0
            });
            await context.SaveChangesAsync();
        }
        byte[] original = SHA256.HashData(await File.ReadAllBytesAsync(DatabasePath));
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => OpenAndDisposeAsync(Location));
        Assert.Equal(StorageFailure.CorruptDatabase, failure.Failure);
        Assert.Null(failure.BackupPath);
        Assert.Empty(Backups());
        Assert.Equal(original, SHA256.HashData(await File.ReadAllBytesAsync(DatabasePath)));
    }

    private static async Task OpenAndDisposeAsync(StorageLocation location)
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(location);
    }

    private static async Task CopySecurelyAsync(string source, string destination)
    {
        using FileStream output = StorageLocation.CreateOrOpen(destination);
        using FileStream input = File.OpenRead(source);
        await input.CopyToAsync(output);
    }

    private static async Task<SqliteConnection> ConnectAsync(string path)
    {
        StorageLocation.PrepareSqliteSidecars(path);
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task SetAddressAsync(string path, string table, string address)
    {
        await using SqliteConnection connection = await ConnectAsync(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = table == "Hosts" ? "UPDATE Hosts SET Address=$value" : "UPDATE Endpoints SET Address=$value";
        command.Parameters.AddWithValue("$value", address);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private string[] Backups() => Directory.GetFiles(directory, "before-migration-*.db").Order().ToArray();

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
