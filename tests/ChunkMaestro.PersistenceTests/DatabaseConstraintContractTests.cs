using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class DatabaseConstraintContractTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "chunkmaestro-constraint-test-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Fleets", "Name")]
    [InlineData("Hosts", "FleetId")]
    [InlineData("Hosts", "Name")]
    [InlineData("Hosts", "Address")]
    [InlineData("Hosts", "RegisteredAt")]
    [InlineData("Instances", "HostId")]
    [InlineData("Instances", "Name")]
    [InlineData("Instances", "ServiceName")]
    [InlineData("Instances", "RegisteredAt")]
    [InlineData("Endpoints", "InstanceId")]
    [InlineData("Endpoints", "Edition")]
    [InlineData("Endpoints", "Address")]
    [InlineData("Endpoints", "Port")]
    [InlineData("Endpoints", "HostId")]
    public async Task RequiredPersistedMetadataRefusesNullWithoutChangingInventory(string table, string column)
    {
        // Identifiers are exclusively the fixed theory data above, never caller input.
        await AssertDeniedWritePreservesInventoryAsync($"UPDATE {table} SET {column}=NULL", 1299);
    }

    [Theory]
    [InlineData("UPDATE Hosts SET Address='mc.example' WHERE Address='other.example'")]
    [InlineData("UPDATE Instances SET ServiceName='minecraft.service' WHERE ServiceName='other.service'")]
    [InlineData("UPDATE Endpoints SET Port=25565 WHERE Port=25566")]
    public async Task CanonicalTargetConflictsCannotOverwriteExistingInventory(string sql)
    {
        await AssertDeniedWritePreservesInventoryAsync(sql, 2067);
    }

    [Theory]
    [InlineData("Hosts", "FleetId")]
    [InlineData("Instances", "HostId")]
    [InlineData("Endpoints", "HostId")]
    [InlineData("Endpoints", "InstanceId")]
    public async Task MissingParentCannotReplacePersistedRelationship(string table, string column)
    {
        await AssertDeniedWritePreservesInventoryAsync($"UPDATE {table} SET {column}=$missing", 787);
    }

    [Theory]
    [InlineData("Fleets")]
    [InlineData("Hosts")]
    [InlineData("Instances")]
    public async Task DeletingReferencedParentIsRefusedWithoutCascadingInventoryLoss(string table)
    {
        await AssertDeniedWritePreservesInventoryAsync($"DELETE FROM {table}", 1811);
    }

    [Theory]
    [InlineData("Edition", -1)]
    [InlineData("Edition", 2)]
    [InlineData("Port", 0)]
    [InlineData("Port", 65536)]
    public async Task InvalidPersistedProtocolOrPortIsRefused(string column, int value)
    {
        await AssertDeniedWritePreservesInventoryAsync($"UPDATE Endpoints SET {column}={value.ToString(CultureInfo.InvariantCulture)}", 275);
    }

    [Theory]
    [InlineData("Endpoints")]
    [InlineData("Instances")]
    public async Task MismatchedEndpointHostIsRefusedWithoutRepairOrNewBackup(string changedTable)
    {
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        InstanceId instance = new InstanceId(Guid.NewGuid());
        Endpoint[] endpoints = [new(EndpointEdition.Java, "mc.example", 25565), new(EndpointEdition.Bedrock, "mc.example", 25565)];
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory)))
        {
            await store.RegisterFleetAsync(fleet, "Fleet");
            await store.RegisterHostAsync(host, fleet, "Primary", "mc.example");
            await store.RegisterHostAsync(new HostId(Guid.NewGuid()), fleet, "Other", "other.example");
            await store.RegisterInstanceAsync(instance, host, "Server", "minecraft.service", endpoints);
        }
        string[] damagedRows;
        await using (SqliteConnection connection = await ConnectAsync())
        {
            using SqliteCommand command = connection.CreateCommand();
            // Both choices leave every individual FK valid. The pair is inconsistent.
            command.CommandText = $"UPDATE {changedTable} SET HostId=(SELECT Id FROM Hosts WHERE Address='other.example')";
            Assert.True(await command.ExecuteNonQueryAsync() > 0);
            command.CommandText = "PRAGMA quick_check";
            Assert.Equal("ok", await command.ExecuteScalarAsync());
            command.CommandText = "PRAGMA foreign_key_check";
            await using (SqliteDataReader references = await command.ExecuteReaderAsync())
            {
                Assert.False(await references.ReadAsync());
            }
            damagedRows = await ReadRowsAsync(connection);
        }
        byte[] databaseHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "fleet.db")));
        string[] backups = Directory.GetFiles(directory, "before-migration-*.db").Order().ToArray();
        FleetStorageException refusal = await Assert.ThrowsAsync<FleetStorageException>(async () =>
        {
            await using LocalFleetStore opened = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory));
        });
        Assert.Equal(StorageFailure.CorruptDatabase, refusal.Failure);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Message));
        Assert.DoesNotContain(refusal.Message, char.IsControl);
        Assert.DoesNotContain(directory, refusal.Message);
        Assert.Equal(databaseHash, SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "fleet.db"))));
        Assert.Equal(backups, Directory.GetFiles(directory, "before-migration-*.db").Order().ToArray());
        await using (SqliteConnection connection = await ConnectAsync())
        {
            Assert.Equal(damagedRows, await ReadRowsAsync(connection));
            // Explicit fixture-only recovery proves refusal releases the controller.
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"UPDATE {changedTable} SET HostId=(SELECT Id FROM Hosts WHERE Address='mc.example')";
            Assert.True(await command.ExecuteNonQueryAsync() > 0);
        }
        await using LocalFleetStore recovered = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory));
        Instance saved = Assert.Single((await recovered.ReadAsync()).Instances);
        Assert.Equal(instance, saved.Id);
        Assert.Equal(host, saved.HostId);
        Assert.Equal(endpoints, saved.Endpoints);
        FleetStorageException conflict = await Assert.ThrowsAsync<FleetStorageException>(() => recovered.RegisterInstanceAsync(
            new InstanceId(Guid.NewGuid()), host, "Conflicting", "other.service", [endpoints[0]]));
        Assert.Equal(StorageFailure.InvalidRegistration, conflict.Failure);
        Assert.Single((await recovered.ReadAsync()).Instances);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public async Task PortBoundariesAndDistinctTransportsSurviveReopen(int port)
    {
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        InstanceId instance = new InstanceId(Guid.NewGuid());
        Endpoint[] endpoints = [new(EndpointEdition.Java, "mc.example", port), new(EndpointEdition.Bedrock, "mc.example", port)];
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory)))
        {
            await store.RegisterFleetAsync(fleet, "Fleet");
            await store.RegisterHostAsync(host, fleet, "Host", "mc.example");
            await store.RegisterInstanceAsync(instance, host, "Server", "minecraft.service", endpoints);
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory));
        Instance saved = Assert.Single((await reopened.ReadAsync()).Instances);
        Assert.Equal(instance, saved.Id);
        Assert.Equal(host, saved.HostId);
        Assert.Equal(endpoints, saved.Endpoints);
        Assert.Equal([EndpointTransport.Tcp, EndpointTransport.Udp], saved.Endpoints.Select(endpoint => endpoint.Transport));
    }

    private async Task AssertDeniedWritePreservesInventoryAsync(string sql, int extendedError)
    {
        FleetInventory before;
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory)))
        {
            FleetId fleet = new FleetId(Guid.NewGuid());
            HostId host = new HostId(Guid.NewGuid());
            await store.RegisterFleetAsync(fleet, "Synthetic fleet");
            await store.RegisterHostAsync(host, fleet, "Primary", "MC.EXAMPLE.");
            await store.RegisterHostAsync(new HostId(Guid.NewGuid()), fleet, "Other", "other.example");
            await store.RegisterInstanceAsync(new InstanceId(Guid.NewGuid()), host, "Primary service", "minecraft.service",
                [new(EndpointEdition.Java, "mc.example", 25565), new(EndpointEdition.Bedrock, "mc.example", 25565)]);
            await store.RegisterInstanceAsync(new InstanceId(Guid.NewGuid()), host, "Other service", "other.service",
                [new(EndpointEdition.Java, "0.0.0.0", 25566)]);
            before = await store.ReadAsync();
        }

        string[] rows;
        await using (SqliteConnection connection = await ConnectAsync())
        {
            rows = await ReadRowsAsync(connection);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$missing", Guid.NewGuid().ToString().ToUpperInvariant());
            SqliteException refusal = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(19, refusal.SqliteErrorCode);
            Assert.Equal(extendedError, refusal.SqliteExtendedErrorCode);
            Assert.Equal(rows, await ReadRowsAsync(connection));
        }

        await using (LocalFleetStore reopened = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(directory)))
        {
            FleetInventory after = await reopened.ReadAsync();
            Assert.Equal(before.Fleets, after.Fleets);
            Assert.Equal(before.Hosts, after.Hosts);
            Assert.Equal(before.Instances.Count, after.Instances.Count);
            foreach (Instance expected in before.Instances)
            {
                Instance actual = Assert.Single(after.Instances, instance => instance.Id == expected.Id);
                Assert.Equal(expected with
                {
                    Endpoints = actual.Endpoints
                }, actual);
                Assert.Equal(expected.Endpoints, actual.Endpoints);
            }
            await using SqliteConnection connection = await ConnectAsync();
            Assert.Equal(rows, await ReadRowsAsync(connection));
        }
    }

    private async Task<SqliteConnection> ConnectAsync()
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "fleet.db"),
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

    private static async Task<string[]> ReadRowsAsync(SqliteConnection connection)
    {
        List<string> rows = [];
        string[] queries =
        [
            "SELECT Id, Name FROM Fleets ORDER BY Id",
            "SELECT Id, FleetId, Name, Address, RegisteredAt FROM Hosts ORDER BY Id",
            "SELECT Id, HostId, Name, ServiceName, RegisteredAt FROM Instances ORDER BY Id",
            "SELECT InstanceId, Edition, Address, Port, HostId FROM Endpoints ORDER BY InstanceId, Edition, Address, Port"
        ];
        foreach (string query in queries)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = query;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // Length-prefixed values preserve column boundaries and NULL distinctly.
                List<string> values = [];
                for (int column = 0; column < reader.FieldCount; column++)
                {
                    string? value = reader.IsDBNull(column) ? null : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture);
                    values.Add(value is null ? "NULL" : $"{value.Length}:{value}");
                }
                rows.Add(string.Join("|", values));
            }
        }
        return rows.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
