using ChunkMaestro.Application;
using ChunkMaestro.Domain;
using ChunkMaestro.Presentation;
using Xunit;

namespace ChunkMaestro.AvaloniaHeadlessTests;

/// <summary>Injected local-service faults; these do not assert a storage implementation defect.</summary>
public sealed class MutationLifecycleTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(StorageFailure.StorageUnavailable)]
    [InlineData(StorageFailure.ControllerInUse)]
    [InlineData(StorageFailure.InvalidLocation)]
    [InlineData(StorageFailure.CorruptDatabase)]
    [InlineData(StorageFailure.NewerSchema)]
    [InlineData(StorageFailure.UnsupportedSchema)]
    [InlineData(StorageFailure.MigrationFailed)]
    public async Task PersistThenInjectedFailureRequiresExplicitSuccessfulReloadWithoutReplay(StorageFailure? failure)
    {
        ControlledInventory store = new();
        await using FleetViewModel model = Create(store);
        await model.OpenAsync();
        string[] cached = model.Rows.ToArray();
        store.WriteFailure = failure is StorageFailure category
            ? new FleetStorageException(category, "injected SQL private-path input", "private-backup")
            : new IOException("injected SQL private-path input");
        store.WriteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task writing = model.RegisterFleetAsync();
        await store.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(model.CanRegister);
        Assert.False(model.CanReload);
        await model.ReloadAsync();
        await model.RegisterHostAsync();
        store.WriteRelease.SetResult();
        await writing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, store.Writes);
        Assert.Equal(cached, model.Rows);
        Assert.Equal(1, store.Reads);
        Assert.False(model.CanRegister);
        Assert.True(model.CanReload);
        Assert.Contains("Reload", model.Error, StringComparison.Ordinal);
        Assert.Contains("unknown", model.Status, StringComparison.Ordinal);
        Assert.Contains("not a host contact", model.Status, StringComparison.Ordinal);
        AssertSanitized(model);
        await AssertWritesBlocked(model, store);
        store.FailRead = true;
        await model.ReloadAsync();
        Assert.Equal(cached, model.Rows);
        Assert.True(model.HasReadFailure);
        await AssertWritesBlocked(model, store);
        store.FailRead = false;
        store.WriteFailure = null;
        await model.ReloadAsync();
        Assert.True(model.CanRegister);
        Assert.False(model.HasReadFailure);
        Assert.Equal("", model.Error);
        Assert.Equal(1, store.Writes);
        Assert.Equal(2, model.Rows.Count);
        await model.RegisterFleetAsync();
        Assert.Equal(2, store.Writes);
    }

    [Fact]
    public async Task InjectedValidationFailureAndInvalidPortRemainCorrectableWithoutReload()
    {
        ControlledInventory store = new()
        {
            WriteFailure = new FleetStorageException(StorageFailure.InvalidRegistration, "injected SQL private-path input")
        };
        await using FleetViewModel model = Create(store);
        await model.OpenAsync();
        await model.RegisterFleetAsync();
        Assert.True(model.CanRegister);
        Assert.Equal(1, store.Reads);
        Assert.Contains("Check names", model.Error, StringComparison.Ordinal);
        AssertSanitized(model);
        store.WriteFailure = null;
        model.FleetName = "Corrected";
        await model.RegisterFleetAsync();
        Assert.Equal(2, store.Writes);
        model.SelectedHost = new Host(new HostId(Guid.NewGuid()), model.Fleets[0].Id,
            "Host", "example.invalid", DateTimeOffset.UtcNow, Observation.Unknown);
        model.JavaPort = "65536";
        await model.RegisterInstanceAsync();
        Assert.Equal(2, store.Writes);
        Assert.True(model.CanRegister);
        model.JavaPort = "25565";
        await model.RegisterInstanceAsync();
        Assert.Equal(3, store.Writes);
    }

    [Fact]
    public async Task SuccessfulWriteWithInjectedReadFailureRequiresReloadWithoutReplay()
    {
        ControlledInventory store = new();
        await using FleetViewModel model = Create(store);
        await model.OpenAsync();
        string[] cached = model.Rows.ToArray();
        store.FailRead = true;
        await model.RegisterFleetAsync();
        Assert.Equal(cached, model.Rows);
        Assert.True(model.HasReadFailure);
        await AssertWritesBlocked(model, store);
        store.FailRead = false;
        await model.ReloadAsync();
        Assert.True(model.CanRegister);
        Assert.Equal(1, store.Writes);
        Assert.Equal(2, model.Rows.Count);
    }

    [Fact]
    public async Task CloseDuringOpeningSharesDisposalAndDoesNotStartRead()
    {
        TaskCompletionSource<IFleetInventory> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlledInventory store = new();
        int opens = 0;
        FleetViewModel model = new(_ =>
        {
            opens++;
            return opened.Task;
        })
        {
            LocalStorageConfirmed = true
        };
        Task opening = model.OpenAsync();
        Task close = model.DisposeAsync().AsTask();
        Task repeatedClose = model.DisposeAsync().AsTask();
        Assert.False(close.IsCompleted);
        Assert.False(repeatedClose.IsCompleted);
        AssertClosed(model);
        await model.OpenAsync();
        await model.ReloadAsync();
        await model.RegisterFleetAsync();
        opened.SetResult(store);
        await Task.WhenAll(opening, close, repeatedClose).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await model.DisposeAsync();
        Assert.Equal(1, opens);
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Writes);
        Assert.Equal(1, store.Disposals);
        AssertClosed(model);
    }

    [Fact]
    public async Task CloseDuringContendedWriteWaitsAndReleasesExactlyOnceWithoutFollowupRead()
    {
        ControlledInventory store = new()
        {
            WriteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
            DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        FleetViewModel model = Create(store);
        await model.OpenAsync();
        Task writing = model.RegisterFleetAsync();
        await store.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await model.RegisterHostAsync();
        await model.ReloadAsync();
        Task close = model.DisposeAsync().AsTask();
        Task repeatedClose = model.DisposeAsync().AsTask();
        Assert.False(close.IsCompleted);
        AssertClosed(model);
        await model.RegisterFleetAsync();
        store.WriteRelease.SetResult();
        await writing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await store.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(close.IsCompleted);
        Assert.False(repeatedClose.IsCompleted);
        store.DisposeRelease.SetResult();
        await Task.WhenAll(close, repeatedClose).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await model.DisposeAsync();
        await model.OpenAsync();
        await model.ReloadAsync();
        await model.RegisterInstanceAsync();
        Assert.Equal(1, store.Writes);
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.Disposals);
        AssertClosed(model);
    }

    private static FleetViewModel Create(ControlledInventory store) => new(_ => Task.FromResult<IFleetInventory>(store))
    {
        LocalStorageConfirmed = true,
        FleetName = "Additional"
    };

    private static async Task AssertWritesBlocked(FleetViewModel model, ControlledInventory store)
    {
        Assert.False(model.CanRegister);
        await model.RegisterFleetAsync();
        await model.RegisterHostAsync();
        await model.RegisterInstanceAsync();
        Assert.Equal(1, store.Writes);
    }

    private static void AssertClosed(FleetViewModel model)
    {
        Assert.False(model.CanOpen);
        Assert.False(model.CanRegister);
        Assert.False(model.CanReload);
        Assert.False(model.CanOperate);
    }

    private static void AssertSanitized(FleetViewModel model)
    {
        foreach (string sensitive in new[]
        {
            "SQL", "private-path", "private-backup", "injected"
        })
        {
            Assert.DoesNotContain(sensitive, model.Error + model.Status, StringComparison.Ordinal);
        }
    }

    private sealed class ControlledInventory : IFleetInventory, IAsyncDisposable
    {
        private readonly List<Fleet> fleets = [new(new FleetId(Guid.NewGuid()), "Cached")];
        public int Reads;
        public int Writes;
        public int Disposals;
        public bool FailRead;
        public Exception? WriteFailure;
        public TaskCompletionSource? WriteRelease;
        public TaskCompletionSource? DisposeRelease;
        public TaskCompletionSource WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<FleetInventory> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            if (FailRead)
            {
                throw new IOException("injected SQL private-path");
            }
            return Task.FromResult(new FleetInventory(fleets.ToArray(), [], [], DateTimeOffset.UtcNow));
        }

        public async Task RegisterFleetAsync(FleetId id, string name, CancellationToken cancellationToken = default)
        {
            Writes++;
            WriteEntered.TrySetResult();
            if (WriteRelease is not null)
            {
                await WriteRelease.Task;
            }
            if (WriteFailure is FleetStorageException exception && exception.Failure == StorageFailure.InvalidRegistration)
            {
                throw WriteFailure;
            }
            fleets.Add(new Fleet(id, name));
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }
        }

        public Task RegisterHostAsync(HostId id, FleetId fleetId, string name, string address, CancellationToken cancellationToken = default)
        {
            return RegisterFleetAsync(new FleetId(id.Value), name, cancellationToken);
        }

        public Task RegisterInstanceAsync(InstanceId id, HostId hostId, string name, string serviceName,
            IReadOnlyList<Endpoint> endpoints, CancellationToken cancellationToken = default)
        {
            return RegisterFleetAsync(new FleetId(id.Value), name, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Disposals++;
            DisposeEntered.TrySetResult();
            if (DisposeRelease is not null)
            {
                await DisposeRelease.Task;
            }
        }
    }
}
