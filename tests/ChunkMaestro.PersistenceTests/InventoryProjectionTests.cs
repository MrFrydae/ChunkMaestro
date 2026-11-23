using ChunkMaestro.Domain;
using ChunkMaestro.Persistence;
using Xunit;

namespace ChunkMaestro.PersistenceTests;

public sealed class InventoryProjectionTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        "cm-inventory-projection-" + Guid.NewGuid().ToString("N"));
    private StorageLocation Location => StorageLocation.ForTests(directory);

    [Fact]
    public async Task MultiInstanceInventoryPreservesAssociationsMetadataAndEndpointOrderAcrossReopen()
    {
        FleetId firstFleet = new FleetId(Guid.NewGuid());
        FleetId secondFleet = new FleetId(Guid.NewGuid());
        HostId firstHost = new HostId(Guid.NewGuid());
        HostId secondHost = new HostId(Guid.NewGuid());
        Dictionary<InstanceId, Instance> expected = new Dictionary<InstanceId, Instance>();
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMilliseconds(-1);
        FleetInventory original;
        await using (LocalFleetStore store = await LocalFleetStore.OpenAsync(Location))
        {
            await store.RegisterFleetAsync(secondFleet, "Second fleet");
            await store.RegisterFleetAsync(firstFleet, "First fleet");
            await store.RegisterHostAsync(secondHost, secondFleet, "Second host", "second.example");
            await store.RegisterHostAsync(firstHost, firstFleet, "First host", "first.example");
            for (int index = 0; index < 6; index++)
            {
                InstanceId id = new InstanceId(Guid.NewGuid());
                HostId host = index % 2 == 0 ? firstHost : secondHost;
                int port = 20000 + index / 2 * 10;
                Endpoint[] endpoints =
                [
                    new(EndpointEdition.Java, $"java-low-{index}.example", port),
                    new(EndpointEdition.Java, $"java-high-{index}.example", port + 2),
                    new(EndpointEdition.Bedrock, $"bedrock-low-{index}.example", port),
                    new(EndpointEdition.Bedrock, $"bedrock-high-{index}.example", port + 1)
                ];
                string name = $"Instance {index}";
                string service = $"minecraft-{index}.service";
                expected.Add(id, new Instance(id, host, name, service, default, Observation.Unknown, endpoints));
                await store.RegisterInstanceAsync(id, host, name, service,
                    [endpoints[3], endpoints[1], endpoints[2], endpoints[0]]);
            }
            original = await store.ReadAsync();
            AssertInventory(original);
            FleetInventory repeated = await store.ReadAsync();
            AssertStable(original, repeated);
        }
        await using LocalFleetStore reopened = await LocalFleetStore.OpenAsync(Location);
        FleetInventory recovered = await reopened.ReadAsync();
        AssertInventory(recovered);
        AssertStable(original, recovered);

        void AssertInventory(FleetInventory inventory)
        {
            Assert.InRange(inventory.RetrievedAt, start, DateTimeOffset.UtcNow);
            Assert.Equal(2, inventory.Fleets.Count);
            Assert.Contains(new Fleet(firstFleet, "First fleet"), inventory.Fleets);
            Assert.Contains(new Fleet(secondFleet, "Second fleet"), inventory.Fleets);
            Assert.Equal(2, inventory.Hosts.Count);
            Host first = Assert.Single(inventory.Hosts, host => host.Id == firstHost);
            Host second = Assert.Single(inventory.Hosts, host => host.Id == secondHost);
            Assert.Equal((firstFleet, "First host", "first.example"), (first.FleetId, first.Name, first.Address));
            Assert.Equal((secondFleet, "Second host", "second.example"), (second.FleetId, second.Name, second.Address));
            foreach (Host host in inventory.Hosts)
            {
                Assert.InRange(host.RegisteredAt, start, inventory.RetrievedAt);
                Assert.Equal(Observation.Unknown, host.Observation);
            }
            Assert.Equal(expected.Count, inventory.Instances.Count);
            Assert.Equal(expected.Count, inventory.Instances.Select(instance => instance.Id).Distinct().Count());
            foreach (Instance instance in inventory.Instances)
            {
                Instance registered = expected[instance.Id];
                Assert.Equal(registered.HostId, instance.HostId);
                Assert.Equal(registered.Name, instance.Name);
                Assert.Equal(registered.ServiceName, instance.ServiceName);
                Assert.InRange(instance.RegisteredAt, start, inventory.RetrievedAt);
                Assert.Equal(Observation.Unknown, instance.Observation);
                Assert.Equal(registered.Endpoints, instance.Endpoints);
                Assert.All(instance.Endpoints, endpoint => Assert.Equal(Observation.Unknown, endpoint.Health));
            }
        }
    }

    [Fact]
    public async Task EmptyReadAndCanceledReadLeaveStoreUsableAndDisposedReadIsRejected()
    {
        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(Location);
        FleetInventory empty = await store.ReadAsync();
        Assert.Empty(empty.Fleets);
        Assert.Empty(empty.Hosts);
        Assert.Empty(empty.Instances);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(cancellation.Token));
        FleetId fleet = new FleetId(Guid.NewGuid());
        await store.RegisterFleetAsync(fleet, "After cancellation");
        Assert.Equal(new Fleet(fleet, "After cancellation"), Assert.Single((await store.ReadAsync()).Fleets));
        await store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ReadAsync());
    }

    private static void AssertStable(FleetInventory expected, FleetInventory actual)
    {
        Assert.Equal(expected.Fleets, actual.Fleets);
        Assert.Equal(expected.Hosts, actual.Hosts);
        Assert.Equal(expected.Instances.Select(instance => instance.Id), actual.Instances.Select(instance => instance.Id));
        foreach (Instance instance in actual.Instances)
        {
            Instance previous = Assert.Single(expected.Instances, candidate => candidate.Id == instance.Id);
            Assert.Equal(previous.RegisteredAt, instance.RegisteredAt);
            Assert.Equal(previous.Endpoints, instance.Endpoints);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
