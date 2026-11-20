using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class InitialEndpointConstraintTests : IDisposable
{
    private const string Initial = "20261114034833_InitialFleet";
    private static readonly Guid Fleet = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Host = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherHost = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Instance = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherInstance = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "chunkmaestro-initial-endpoint-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("InstanceId", 1299)]
    [InlineData("Edition", 1299)]
    [InlineData("Address", 1299)]
    [InlineData("Port", 1299)]
    [InlineData("HostId", 1299)]
    [InlineData("missing-instance", 787)]
    [InlineData("missing-host", 787)]
    [InlineData("identity", 1555)]
    [InlineData("host-transport-port", 2067)]
    public async Task InitialEndpointRefusalPreservesRowsThroughUpgradeAndBackupRecovery(string contract, int extendedError)
    {
        string source = Path.Combine(root, "source");
        await SeedAsync(source);
        string before = await AssertRefusalAsync(source, contract, extendedError);
        FleetInventory upgraded = await UpgradeAndReadAsync(source, before);
        string backup = Assert.Single(Directory.GetFiles(source, "before-migration-*.db"));

        // Exercise the compatible pre-upgrade backup in a separate disposable owner directory.
        // The original backup stays untouched and no downgrade/predecessor is manufactured.
        byte[] backupBefore = await File.ReadAllBytesAsync(backup);
        string recovered = Path.Combine(root, "reviewed-recovery");
        Prepare(recovered);
        File.Copy(backup, Path.Combine(recovered, "fleet.db"), true);
        Assert.Equal(before, await AssertRefusalAsync(recovered, contract, extendedError));
        FleetInventory restored = await UpgradeAndReadAsync(recovered, before);
        Assert.Equal(upgraded.Fleets, restored.Fleets);
        Assert.Equal(upgraded.Hosts, restored.Hosts);
        Assert.Equal(upgraded.Instances.Select(instance => instance.Id), restored.Instances.Select(instance => instance.Id));
        Assert.Equal(backupBefore, await File.ReadAllBytesAsync(backup));
    }

    private static async Task<string> AssertRefusalAsync(string directory, string contract, int extendedError)
    {
        await using SqliteConnection connection = await ConnectAsync(directory);
        await using FleetDbContext context = Context(connection);
        Assert.Equal([Initial], await context.Database.GetAppliedMigrationsAsync());
        string before = await RowsAsync(connection);
        using SqliteCommand command = connection.CreateCommand();
        // SQL identifiers come only from the closed theory cases above.
        command.CommandText = contract switch
        {
            "missing-instance" => "UPDATE Endpoints SET InstanceId=$missing WHERE Edition=0",
            "missing-host" => "UPDATE Endpoints SET HostId=$missing WHERE Edition=0",
            // A different valid host avoids the host/edition/port unique index: only endpoint
            // identity can reject this duplicate. The refused row never reaches upgrade.
            "identity" => "INSERT INTO Endpoints SELECT InstanceId, Edition, Address, Port, $otherHost FROM Endpoints WHERE Edition=0",
            // A different instance and address avoid the primary key, isolating host port reservation.
            "host-transport-port" => "INSERT INTO Endpoints SELECT $otherInstance, Edition, 'alias.fixture.example', Port, HostId FROM Endpoints WHERE Edition=0",
            _ => $"UPDATE Endpoints SET {contract}=NULL WHERE Edition=0"
        };
        command.Parameters.AddWithValue("$missing", Guid.NewGuid().ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("$otherHost", OtherHost.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("$otherInstance", OtherInstance.ToString().ToUpperInvariant());
        SqliteException refusal = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(19, refusal.SqliteErrorCode);
        Assert.Equal(extendedError, refusal.SqliteExtendedErrorCode);
        Assert.Equal(before, await RowsAsync(connection));
        return before;
    }

    private static async Task<FleetInventory> UpgradeAndReadAsync(string directory, string before)
    {
        FleetInventory inventory;
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory)))
        {
            inventory = await store.ReadAsync();
            Assert.Equal(new FleetId(Fleet), Assert.Single(inventory.Fleets).Id);
            Assert.Equal(2, inventory.Hosts.Count);
            Assert.Equal(2, inventory.Instances.Count);
            Instance saved = Assert.Single(inventory.Instances, instance => instance.Id == new InstanceId(Instance));
            Assert.Equal(new HostId(Host), saved.HostId);
            Assert.Equal("minecraft@initial.service", saved.ServiceName);
            Assert.Equal([new Endpoint(EndpointEdition.Java, "fixture.example", 25565),
                new Endpoint(EndpointEdition.Bedrock, "fixture.example", 25565)], saved.Endpoints);
            Instance other = Assert.Single(inventory.Instances, instance => instance.Id == new InstanceId(OtherInstance));
            Assert.Equal(new HostId(Host), other.HostId);
            Assert.Empty(other.Endpoints);
        }
        await using SqliteConnection connection = await ConnectAsync(directory);
        Assert.Equal(before, await RowsAsync(connection));
        return inventory;
    }

    private static async Task SeedAsync(string directory)
    {
        Prepare(directory);
        await using SqliteConnection connection = await ConnectAsync(directory);
        await using FleetDbContext context = Context(connection);
        await context.GetService<IMigrator>().MigrateAsync(Initial);
        context.Fleets.Add(new FleetRow
        {
            Id = Fleet,
            Name = "Initial fixture fleet"
        });
        foreach (Guid host in new Guid[]
        {
            Host, OtherHost
        })
        {
            context.Hosts.Add(new HostRow
            {
                Id = host,
                FleetId = Fleet,
                Name = "Synthetic host",
                Address = host == Host ? "fixture.example" : "other.fixture.example",
                RegisteredAt = 1730000000123
            });
        }
        foreach (Guid instance in new Guid[]
        {
            Instance, OtherInstance
        })
        {
            context.Instances.Add(new InstanceRow
            {
                Id = instance,
                HostId = Host,
                Name = "Synthetic instance",
                ServiceName = instance == Instance ? "minecraft@initial.service" : "minecraft@other.service",
                RegisteredAt = 1730000000456
            });
        }
        foreach (int edition in new int[]
        {
            0, 1
        })
        {
            context.Endpoints.Add(new EndpointRow
            {
                InstanceId = Instance,
                HostId = Host,
                Edition = edition,
                Address = "fixture.example",
                Port = 25565
            });
        }
        await context.SaveChangesAsync();
    }

    private static void Prepare(string directory)
    {
        StorageLocation.ForTests(directory).Prepare();
        using (StorageLocation.CreateOrOpen(Path.Combine(directory, "fleet.db")))
        {
        }
    }

    private static FleetDbContext Context(SqliteConnection connection)
    {
        return new FleetDbContext(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(connection).Options);
    }

    private static async Task<SqliteConnection> ConnectAsync(string directory)
    {
        string database = Path.Combine(directory, "fleet.db");
        StorageLocation.PrepareSqliteSidecars(database);
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        return connection;
    }

    private static async Task<string> RowsAsync(SqliteConnection connection)
    {
        List<string?[]> rows = [];
        foreach (string table in new string[]
        {
            "Fleets", "Hosts", "Instances", "Endpoints"
        })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY 1,2";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string?[] row = new string?[reader.FieldCount + 1];
                row[0] = table;
                for (int column = 0; column < reader.FieldCount; column++)
                {
                    row[column + 1] = reader.IsDBNull(column) ? null : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture);
                }
                rows.Add(row);
            }
        }
        return JsonSerializer.Serialize(rows);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
