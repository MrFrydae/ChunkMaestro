using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class ManagedStorageQualificationTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "chunkmaestro-managed-" + Guid.NewGuid().ToString("N"));
    private StorageLocation Location => StorageLocation.ForTests(directory);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("visible\nsecret")]
    [InlineData("visible\0secret")]
    public async Task InvalidLabelsLeaveEveryRegistrationBoundaryUnchanged(string? label)
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        await DeniedAsync(() => store.RegisterFleetAsync(fleet, label!));
        Assert.Empty((await store.ReadAsync()).Fleets);
        await store.RegisterFleetAsync(fleet, "Fleet");
        await DeniedAsync(() => store.RegisterHostAsync(host, fleet, label!, "localhost"));
        Assert.Empty((await store.ReadAsync()).Hosts);
        await store.RegisterHostAsync(host, fleet, "Host", "localhost");
        await DeniedAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), host, label!, "one.service", [new(EndpointEdition.Java, "localhost", 25565)]));
        Assert.Empty((await store.ReadAsync()).Instances);
    }

    [Fact]
    public async Task LabelBoundariesTrimAndPersistWithoutTruncation()
    {
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        string maximum = new string('x', 120);
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
            await DeniedAsync(() => store.RegisterFleetAsync(fleet, maximum + "x"));
            await store.RegisterFleetAsync(fleet, maximum);
            await DeniedAsync(() => store.RegisterHostAsync(host, fleet, maximum + "x", "localhost"));
            await store.RegisterHostAsync(host, fleet, "  Host  ", "localhost");
            await DeniedAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), host, maximum + "x", "one.service", [new(EndpointEdition.Java, "localhost", 1)]));
            await store.RegisterInstanceAsync(new(Guid.NewGuid()), host, "  Instance  ", "one.service", [new(EndpointEdition.Java, "localhost", 1)]);
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        FleetInventory inventory = await reopened.ReadAsync();
        Assert.Equal(maximum, Assert.Single(inventory.Fleets).Name);
        Assert.Equal("Host", Assert.Single(inventory.Hosts).Name);
        Assert.Equal("Instance", Assert.Single(inventory.Instances).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" host.example")]
    [InlineData("host.example ")]
    [InlineData("host_name.example")]
    [InlineData("user:password@host.example")]
    public async Task InvalidHostAndEndpointAddressesCannotPartiallyRegister(string? address)
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        await store.RegisterFleetAsync(fleet, "Fleet");
        await DeniedAsync(() => store.RegisterHostAsync(host, fleet, "Host", address!));
        Assert.Empty((await store.ReadAsync()).Hosts);
        await store.RegisterHostAsync(host, fleet, "Host", "localhost");
        await DeniedAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), host, "Instance", "one.service",
            [new(EndpointEdition.Java, "localhost", 1), new(EndpointEdition.Bedrock, address!, 2)]));
        Assert.Empty((await store.ReadAsync()).Instances);
    }

    [Fact]
    public async Task EveryEmptyIdentityAndMissingParentIsRejectedWithoutRows()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        Endpoint[] endpoints = [new(EndpointEdition.Java, "localhost", 25565)];
        await DeniedAsync(() => store.RegisterHostAsync(new(Guid.Empty), fleet, "Host", "localhost"));
        await DeniedAsync(() => store.RegisterHostAsync(host, new(Guid.Empty), "Host", "localhost"));
        await DeniedAsync(() => store.RegisterInstanceAsync(new(Guid.Empty), host, "Instance", "one.service", endpoints));
        await DeniedAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), new(Guid.Empty), "Instance", "one.service", endpoints));
        await DeniedAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), host, "Instance", "one.service", endpoints));
        FleetInventory inventory = await store.ReadAsync();
        Assert.Empty(inventory.Fleets);
        Assert.Empty(inventory.Hosts);
        Assert.Empty(inventory.Instances);
        await store.RegisterFleetAsync(fleet, "Fleet");
        await store.RegisterHostAsync(host, fleet, "Host", "localhost");
        await store.RegisterInstanceAsync(new(Guid.NewGuid()), host, "Instance", "one.service", endpoints);
        Assert.Single((await store.ReadAsync()).Instances);
    }

    [Fact]
    public async Task EndpointCountAndNullBoundariesAreAtomicAndAllowMaximum()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        await store.RegisterFleetAsync(fleet, "Fleet");
        await store.RegisterHostAsync(host, fleet, "Host", "localhost");
        Endpoint[] maximum = Enumerable.Range(1, 16).Select(port => new Endpoint(EndpointEdition.Java, "localhost", port)).ToArray();
        IReadOnlyList<Endpoint>?[] invalid = [null, Array.Empty<Endpoint>(), maximum.Append(new(EndpointEdition.Java, "localhost", 17)).ToArray(), [null!]];
        foreach (IReadOnlyList<Endpoint>? endpoints in invalid)
        {
            await DeniedAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), host, "Instance", "one.service", endpoints!));
            Assert.Empty((await store.ReadAsync()).Instances);
        }
        await DeniedAsync(() => store.RegisterInstanceAsync(new(Guid.NewGuid()), host, "Instance", null!, maximum));
        await store.RegisterInstanceAsync(new(Guid.NewGuid()), host, "Instance", "one.service", maximum);
        Assert.Equal(maximum, Assert.Single((await store.ReadAsync()).Instances).Endpoints);
    }

    [Fact]
    public async Task LateEndpointConflictRollsBackServiceAndEarlierEndpointsAcrossReopen()
    {
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        InstanceId retry = new InstanceId(Guid.NewGuid());
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
            await store.RegisterFleetAsync(fleet, "Fleet");
            await store.RegisterHostAsync(host, fleet, "Host", "localhost");
            await store.RegisterInstanceAsync(new(Guid.NewGuid()), host, "Existing", "existing.service", [new(EndpointEdition.Java, "localhost", 65535)]);
            await DeniedAsync(() => store.RegisterInstanceAsync(retry, host, "Rejected", "retry.service",
                [new(EndpointEdition.Java, "localhost", 1), new(EndpointEdition.Java, "0.0.0.0", 65535)]));
            Assert.Single((await store.ReadAsync()).Instances);
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        await reopened.RegisterInstanceAsync(retry, host, "Retry", "retry.service", [new(EndpointEdition.Java, "localhost", 1)]);
        FleetInventory inventory = await reopened.ReadAsync();
        Assert.Equal(2, inventory.Instances.Count);
        Assert.Equal(2, inventory.Instances.Sum(instance => instance.Endpoints.Count));
        Assert.Equal("Retry", Assert.Single(inventory.Instances, instance => instance.Id == retry).Name);
    }

    [Fact]
    public async Task CanonicalIpv6CollisionAndDuplicateEndpointDoNotConsumeIdentity()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        await store.RegisterFleetAsync(fleet, "Fleet");
        await store.RegisterHostAsync(host, fleet, "Host", "2001:0DB8:0000:0000:0000:0000:0000:0001");
        await DeniedAsync(() => store.RegisterHostAsync(new(Guid.NewGuid()), fleet, "Alias", "2001:db8::1"));
        InstanceId id = new InstanceId(Guid.NewGuid());
        await DeniedAsync(() => store.RegisterInstanceAsync(id, host, "Duplicate", "one.service",
            [new(EndpointEdition.Java, "::1", 1), new(EndpointEdition.Java, "::", 1)]));
        await store.RegisterInstanceAsync(id, host, "Accepted", "one.service", [new(EndpointEdition.Java, "2001:0DB8::1", 65535)]);
        FleetInventory inventory = await store.ReadAsync();
        Assert.Equal("2001:db8::1", Assert.Single(inventory.Hosts).Address);
        Assert.Equal("2001:db8::1", Assert.Single(Assert.Single(inventory.Instances).Endpoints).Address);
    }

    [Fact]
    public async Task CanceledOperationsAndRepeatedDisposalPreserveInventoryAndReleaseOwnership()
    {
        FleetId fleet = new FleetId(Guid.NewGuid());
        LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        try
        {
            await store.RegisterFleetAsync(fleet, "Preserved");
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalFleetStore.OpenAsync(Location, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RegisterHostAsync(new(Guid.NewGuid()), fleet, "Canceled", "localhost", cancellation.Token));
            Assert.Empty((await store.ReadAsync()).Hosts);
        }
        finally
        {
            await store.DisposeAsync();
            await store.DisposeAsync();
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ReadAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.RegisterFleetAsync(new(Guid.NewGuid()), "Disposed"));
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        Assert.Equal(fleet, Assert.Single((await reopened.ReadAsync()).Fleets).Id);
        Assert.Empty((await reopened.ReadAsync()).Hosts);
    }

    [Fact]
    public async Task LiveSchemaLossIsSanitizedAndNeverRecreatedOnReadOrWrite()
    {
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
            await using SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "fleet.db"),
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE Fleets";
            await command.ExecuteNonQueryAsync();
            FleetStorageException read = await Assert.ThrowsAsync<FleetStorageException>(() => store.ReadAsync());
            FleetStorageException write = await Assert.ThrowsAsync<FleetStorageException>(() => store.RegisterFleetAsync(new(Guid.NewGuid()), "private-input"));
            foreach (FleetStorageException failure in new[]
            {
                read,
                write
            })
            {
                Assert.Equal(StorageFailure.StorageUnavailable, failure.Failure);
                Assert.Null(failure.InnerException);
                Assert.DoesNotContain(directory, failure.ToString());
                Assert.DoesNotContain("private-input", failure.ToString());
                Assert.DoesNotContain("SQLite Error", failure.ToString());
            }
        }
        FleetStorageException reopen = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
        Assert.Equal(StorageFailure.UnsupportedSchema, reopen.Failure);
    }

    [Fact]
    public async Task MaximumDnsLengthAndCanonicalEndpointSurviveReopenWithMetadata()
    {
        string maximum = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
        Assert.Equal(253, maximum.Length);
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        InstanceId id = new InstanceId(Guid.NewGuid());
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
            await store.RegisterFleetAsync(fleet, "Fleet");
            await DeniedAsync(() => store.RegisterHostAsync(host, fleet, "Host", maximum + "x"));
            await store.RegisterHostAsync(host, fleet, "Host", maximum.ToUpperInvariant());
            await DeniedAsync(() => store.RegisterInstanceAsync(id, host, "Instance", "minecraft@world.service", [new(EndpointEdition.Bedrock, maximum + "x", 65535)]));
            await store.RegisterInstanceAsync(id, host, "Instance", "minecraft@world.service", [new(EndpointEdition.Bedrock, maximum.ToUpperInvariant(), 65535)]);
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        FleetInventory inventory = await reopened.ReadAsync();
        Host registeredHost = Assert.Single(inventory.Hosts);
        Assert.Equal(host, registeredHost.Id);
        Assert.Equal(fleet, registeredHost.FleetId);
        Assert.Equal("Host", registeredHost.Name);
        Assert.Equal(maximum, registeredHost.Address);
        Assert.InRange(registeredHost.RegisteredAt, before, DateTimeOffset.UtcNow);
        Instance instance = Assert.Single(inventory.Instances);
        Assert.Equal(id, instance.Id);
        Assert.Equal(host, instance.HostId);
        Assert.Equal("Instance", instance.Name);
        Assert.Equal("minecraft@world.service", instance.ServiceName);
        Assert.InRange(instance.RegisteredAt, before, DateTimeOffset.UtcNow);
        Assert.Equal(Observation.Unknown, instance.Observation);
        Endpoint endpoint = Assert.Single(instance.Endpoints);
        Assert.Equal(EndpointEdition.Bedrock, endpoint.Edition);
        Assert.Equal(EndpointTransport.Udp, endpoint.Transport);
        Assert.Equal(maximum, endpoint.Address);
        Assert.Equal(65535, endpoint.Port);
        Assert.InRange(inventory.RetrievedAt, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task DuplicatePrimaryKeysPreserveOriginalNamesAndDoNotLeakEndpoints()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetId fleet = new FleetId(Guid.NewGuid());
        HostId host = new HostId(Guid.NewGuid());
        InstanceId instance = new InstanceId(Guid.NewGuid());
        await store.RegisterFleetAsync(fleet, "Original fleet");
        await DeniedAsync(() => store.RegisterFleetAsync(fleet, "Replacement fleet"));
        await store.RegisterHostAsync(host, fleet, "Original host", "localhost");
        await DeniedAsync(() => store.RegisterHostAsync(host, fleet, "Replacement host", "other.example"));
        await store.RegisterInstanceAsync(instance, host, "Original instance", "one.service", [new(EndpointEdition.Java, "localhost", 1)]);
        await DeniedAsync(() => store.RegisterInstanceAsync(instance, host, "Replacement instance", "two.service", [new(EndpointEdition.Java, "localhost", 2)]));
        FleetInventory inventory = await store.ReadAsync();
        Assert.Equal("Original fleet", Assert.Single(inventory.Fleets).Name);
        Assert.Equal("Original host", Assert.Single(inventory.Hosts).Name);
        Assert.Equal("Original instance", Assert.Single(inventory.Instances).Name);
        Assert.Equal(1, Assert.Single(Assert.Single(inventory.Instances).Endpoints).Port);
        await store.RegisterInstanceAsync(new(Guid.NewGuid()), host, "Explicit new instance", "two.service", [new(EndpointEdition.Java, "localhost", 2)]);
        Assert.Equal(2, (await store.ReadAsync()).Instances.Count);
    }

    [Fact]
    public async Task SqliteQuickCheckRowCorruptionIsRefusedWithoutCertifyingBackupOrRepair()
    {
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
        }
        await using (SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "fleet.db"),
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Damaged (Value INTEGER CHECK (Value > 0)); PRAGMA ignore_check_constraints=ON; INSERT INTO Damaged VALUES (-1); PRAGMA ignore_check_constraints=OFF";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "PRAGMA quick_check";
            Assert.NotEqual("ok", await command.ExecuteScalarAsync());
        }
        string path = Path.Combine(directory, "fleet.db");
        byte[] before = await File.ReadAllBytesAsync(path);
        string[] backups = Directory.GetFiles(directory, "before-migration-*.db");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
            Assert.Equal(StorageFailure.CorruptDatabase, failure.Failure);
            Assert.Contains("Preserve", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.Equal(backups, Directory.GetFiles(directory, "before-migration-*.db"));
        }
    }

    [Fact]
    public async Task UnversionedPopulatedDatabaseIsPreservedWithoutMigrationOrBackupCertification()
    {
        Location.Prepare();
        string path = Path.Combine(directory, "fleet.db");
        using (StorageLocation.CreateOrOpen(path))
        {
        }
        await using (SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Unversioned (Value TEXT); INSERT INTO Unversioned VALUES ('preserved')";
            await command.ExecuteNonQueryAsync();
        }
        byte[] before = await File.ReadAllBytesAsync(path);
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(() => LocalFleetStore.OpenAsync(Location));
        Assert.Equal(StorageFailure.UnsupportedSchema, failure.Failure);
        Assert.Contains("Unversioned", failure.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory, "before-migration-*.db"));
    }

    private static async Task DeniedAsync(Func<Task> action)
    {
        FleetStorageException failure = await Assert.ThrowsAsync<FleetStorageException>(action);
        Assert.Equal(StorageFailure.InvalidRegistration, failure.Failure);
        Assert.Null(failure.InnerException);
        Assert.Null(failure.BackupPath);
        Assert.DoesNotContain("SQLite", failure.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
