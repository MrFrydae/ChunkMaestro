using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using ChunkMaestro.Application;
using ChunkMaestro.Desktop;
using ChunkMaestro.Domain;
using ChunkMaestro.Presentation;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(ChunkMaestro.AvaloniaHeadlessTests.TestApp))]
namespace ChunkMaestro.AvaloniaHeadlessTests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class ShellTests
{
    [AvaloniaFact]
    public async Task ConsentGatesInitializationAndEmptyStateIsHonest()
    {
        MemoryInventory store = new();
        int calls = 0;
        await using FleetViewModel model = new(confirmed =>
{
    Assert.True(confirmed);
    calls++;
    return Task.FromResult<IFleetInventory>(store);
});
        MainWindow window = new()
        {
            DataContext = model
        };
        window.Show();
        Assert.False(window.FindControl<Button>("OpenStorage")!.IsEnabled);
        await model.OpenAsync();
        Assert.Equal(0, calls);
        model.LocalStorageConfirmed = true;
        await model.OpenAsync();
        Assert.Equal(1, calls);
        Assert.True(model.IsEmpty);
        Assert.Contains("Local cached metadata", model.Status, StringComparison.Ordinal);
        Assert.True(window.FindControl<TextBlock>("EmptyState")!.IsVisible);
        Assert.False(window.FindControl<Button>("RemoteOperations")!.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RegistrationReloadsHierarchyAndSeparatesTransportsWithoutAuthority()
    {
        MemoryInventory store = new();
        await using FleetViewModel model = new(_ => Task.FromResult<IFleetInventory>(store))
        {
            LocalStorageConfirmed = true
        };
        MainWindow window = new()
        {
            DataContext = model
        };
        window.Show();
        await model.OpenAsync();
        model.FleetName = "Home";
        await model.RegisterFleetAsync();
        model.SelectedFleet = model.Fleets.Single();
        model.HostName = "Host";
        model.HostAddress = "example.invalid";
        await model.RegisterHostAsync();
        model.SelectedHost = model.Hosts.Single();
        model.InstanceName = "Survival";
        model.BedrockPort = "25565";
        await model.RegisterInstanceAsync();
        Assert.Single(store.Instances);
        Assert.Equal(2, store.Instances[0].Endpoints.Count);
        Assert.Contains(model.Rows, row => row.Contains("Java TCP", StringComparison.Ordinal));
        Assert.Contains(model.Rows, row => row.Contains("Bedrock UDP", StringComparison.Ordinal));
        Assert.Contains(model.Rows, row => row.Contains("Not enrolled / unknown", StringComparison.Ordinal));
        Assert.False(model.CanOperate);
        Assert.False(window.FindControl<TextBlock>("EmptyState")!.IsVisible);
        Assert.False(window.FindControl<Button>("RemoteOperations")!.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReadFailureRetainsCachedRowsAndBlocksWritesUntilSuccessfulReload()
    {
        MemoryInventory store = new();
        await using FleetViewModel model = new(_ => Task.FromResult<IFleetInventory>(store))
        {
            LocalStorageConfirmed = true
        };
        MainWindow window = new()
        {
            DataContext = model
        };
        window.Show();
        await model.OpenAsync();
        model.FleetName = "Home";
        await model.RegisterFleetAsync();
        string[] previous = model.Rows.ToArray();
        store.FailRead = true;
        await model.ReloadAsync();
        Assert.Equal(previous, model.Rows);
        Assert.Contains("stale", model.Status, StringComparison.Ordinal);
        Assert.False(model.CanRegister);
        Assert.True(model.CanReload);
        Assert.DoesNotContain("SQL", window.FindControl<TextBlock>("Error")!.Text!, StringComparison.Ordinal);
        await model.RegisterFleetAsync();
        Assert.Single(store.Fleets);
        store.FailRead = false;
        await model.ReloadAsync();
        Assert.True(model.CanRegister);
        Assert.Equal("", model.Error);
        window.Close();
    }

    [AvaloniaFact]
    public async Task InitializationFailureIsSanitizedAndCanRetryWithoutReset()
    {
        int attempts = 0;
        await using FleetViewModel model = new(_ =>
{
    attempts++;
    throw new FleetStorageException(StorageFailure.MigrationFailed, "SQL secret path");
})
        {
            LocalStorageConfirmed = true
        };
        MainWindow window = new()
        {
            DataContext = model
        };
        window.Show();
        await model.OpenAsync();
        Assert.Contains("Preserve", model.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", model.Error, StringComparison.Ordinal);
        Assert.True(model.CanOpen);
        Assert.False(model.CanRegister);
        Assert.False(model.CanOperate);
        await model.OpenAsync();
        Assert.Equal(2, attempts);
        window.Close();
    }

    [AvaloniaFact]
    public async Task InvalidPortNeverReachesApplicationServiceAndLabelsAndFocusExist()
    {
        MemoryInventory store = new();
        await using FleetViewModel model = new(_ => Task.FromResult<IFleetInventory>(store))
        {
            LocalStorageConfirmed = true
        };
        MainWindow window = new()
        {
            DataContext = model
        };
        window.Show();
        await model.OpenAsync();
        model.SelectedHost = new Host(new HostId(Guid.NewGuid()), new FleetId(Guid.NewGuid()), "Host", "example.invalid", DateTimeOffset.UtcNow, Observation.Unknown);
        model.JavaPort = "65536";
        await model.RegisterInstanceAsync();
        Assert.Empty(store.Instances);
        Assert.Contains("Check names", model.Error, StringComparison.Ordinal);
        TextBox port = window.FindControl<TextBox>("JavaPortInput")!;
        Assert.Equal("Java TCP port", AutomationProperties.GetName(port));
        port.Focus();
        Assert.True(port.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void SystemDefaultSupportsLightAndDarkSurfacesWithExactAccents()
    {
        VerifyThemes();
        static void VerifyThemes()
        {
            MainWindow window = new();
            Assert.Equal(ThemeVariant.Default, window.RequestedThemeVariant);
            window.RequestedThemeVariant = ThemeVariant.Light;
            window.Show();
            Assert.Equal(Color.Parse("#FAFAF9"), ((ISolidColorBrush)window.Background!).Color);
            Assert.True(window.TryFindResource("ShellAccent", ThemeVariant.Light, out object? light));
            Assert.Equal(Color.Parse("#2563EB"), ((ISolidColorBrush)light!).Color);
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Assert.Equal(Color.Parse("#242427"), ((ISolidColorBrush)window.Background!).Color);
            Assert.True(window.TryFindResource("ShellAccent", ThemeVariant.Dark, out object? dark));
            Assert.Equal(Color.Parse("#60A5FA"), ((ISolidColorBrush)dark!).Color);
            window.Close();
        }
    }

    [Fact]
    public async Task ClosingDuringOpenWaitsAndReleasesSession()
    {
        TaskCompletionSource<IFleetInventory> opened = new();
        MemoryInventory store = new();
        FleetViewModel model = new(_ => opened.Task)
        {
            LocalStorageConfirmed = true
        };
        Task opening = model.OpenAsync();
        Task closing = model.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        opened.SetResult(store);
        await opening;
        await closing;
        Assert.True(store.Disposed);
        Assert.False(model.CanRegister);
    }
}

internal sealed class MemoryInventory : IFleetInventory, IAsyncDisposable
{
    public List<Fleet> Fleets
    {
        get;
    } = [];
    public List<Host> Hosts
    {
        get;
    } = [];
    public List<Instance> Instances
    {
        get;
    } = [];
    public bool FailRead
    {
        get; set;
    }

    public bool Disposed
    {
        get; private set;
    }

    public Task<FleetInventory> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (FailRead)
        {
            throw new IOException("SQL private path");
        }

        return Task.FromResult(new FleetInventory(Fleets.ToArray(), Hosts.ToArray(), Instances.ToArray(), DateTimeOffset.UtcNow));
    }
    public Task RegisterFleetAsync(FleetId id, string name, CancellationToken cancellationToken = default)
    {
        Fleets.Add(new Fleet(id, name));
        return Task.CompletedTask;
    }

    public Task RegisterHostAsync(HostId id, FleetId fleetId, string name, string address, CancellationToken cancellationToken = default)

    {
        Hosts.Add(new Host(id, fleetId, name, address, DateTimeOffset.UtcNow, Observation.Unknown));
        return Task.CompletedTask;
    }

    public Task RegisterInstanceAsync(InstanceId id, HostId hostId, string name, string serviceName, IReadOnlyList<Endpoint> endpoints, CancellationToken cancellationToken = default)

    {
        Instances.Add(new Instance(id, hostId, name, serviceName, DateTimeOffset.UtcNow, Observation.Unknown, endpoints));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

}
