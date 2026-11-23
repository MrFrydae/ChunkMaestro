using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ChunkMaestro.Application;
using ChunkMaestro.Desktop;
using ChunkMaestro.Domain;
using ChunkMaestro.Presentation;
using Xunit;

namespace ChunkMaestro.AvaloniaHeadlessTests;

public sealed class WindowCloseTests
{
    [AvaloniaFact]
    public async Task CleanupFailureShowsSanitizedGuidanceAndAllowsExplicitExitWithoutRetry()
    {
        CloseInventory store = new()
        {
            FailDispose = true
        };
        FleetViewModel model = new(_ => Task.FromResult<IFleetInventory>(store))
        {
            LocalStorageConfirmed = true
        };
        await model.OpenAsync();
        MainWindow window = new()
        {
            DataContext = model
        };
        WindowCloseController close = new(window, model);
        TaskCompletionSource closed = ClosedSignal(window);
        window.Show();
        window.Close();
        await DrainAsync();
        Assert.True(close.CleanupFailed);
        Assert.True(window.IsVisible);
        Assert.True(window.IsEnabled);
        TextBlock message = window.FindControl<TextBlock>("CloseFailure")!;
        Assert.True(message.IsVisible);
        Assert.Contains("Close this window again", message.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("private", message.Text!, StringComparison.Ordinal);
        Assert.False(model.CanRegister);
        Assert.False(model.CanReload);
        Assert.False(model.CanOpen);
        await model.RegisterFleetAsync();
        Assert.Empty(store.Memory.Fleets);
        Assert.Equal(1, store.Disposes);
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(window.IsVisible);
        Assert.Equal(1, store.Disposes);
    }

    [AvaloniaFact]
    public async Task SuccessfulCleanupClosesAfterTheOriginalClosingEvent()
    {
        CloseInventory store = new();
        FleetViewModel model = new(_ => Task.FromResult<IFleetInventory>(store))
        {
            LocalStorageConfirmed = true
        };
        await model.OpenAsync();
        MainWindow window = new()
        {
            DataContext = model
        };
        WindowCloseController close = new(window, model);
        TaskCompletionSource closed = ClosedSignal(window);
        window.Show();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(window.IsVisible);
        Assert.False(close.CleanupFailed);
        Assert.Equal(1, store.Disposes);
    }

    [AvaloniaFact]
    public async Task RepeatedCloseWhileCleanupIsPendingDoesNotStartAnotherDisposal()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CloseInventory store = new()
        {
            Release = release.Task
        };
        FleetViewModel model = new(_ => Task.FromResult<IFleetInventory>(store))
        {
            LocalStorageConfirmed = true
        };
        await model.OpenAsync();
        MainWindow window = new()
        {
            DataContext = model
        };
        WindowCloseController close = new(window, model);
        TaskCompletionSource closed = ClosedSignal(window);
        window.Show();
        window.Close();
        window.Close();
        Assert.True(window.IsVisible);
        Assert.False(window.IsEnabled);
        Assert.Equal(1, store.Disposes);
        release.SetResult();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(window.IsVisible);
        Assert.False(close.CleanupFailed);
        Assert.Equal(1, store.Disposes);
    }

    private static async Task DrainAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
        }, DispatcherPriority.Background, TestContext.Current.CancellationToken);
    }

    private static TaskCompletionSource ClosedSignal(Window window)
    {
        TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => signal.TrySetResult();
        return signal;
    }

    private sealed class CloseInventory : IFleetInventory, IAsyncDisposable
    {
        public MemoryInventory Memory
        {
            get;
        } = new();
        public bool FailDispose
        {
            get;
            init;
        }
        public Task? Release
        {
            get;
            init;
        }
        public int Disposes
        {
            get;
            private set;
        }

        public Task<FleetInventory> ReadAsync(CancellationToken cancellationToken = default) => Memory.ReadAsync(cancellationToken);
        public Task RegisterFleetAsync(FleetId id, string name, CancellationToken cancellationToken = default) => Memory.RegisterFleetAsync(id, name, cancellationToken);
        public Task RegisterHostAsync(HostId id, FleetId fleet, string name, string address, CancellationToken cancellationToken = default) => Memory.RegisterHostAsync(id, fleet, name, address, cancellationToken);
        public Task RegisterInstanceAsync(InstanceId id, HostId host, string name, string service, IReadOnlyList<Endpoint> endpoints, CancellationToken cancellationToken = default) => Memory.RegisterInstanceAsync(id, host, name, service, endpoints, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposes++;
            if (FailDispose)
            {
                return ValueTask.FromException(new IOException("injected private SQL and backup path"));
            }
            return Release is null ? ValueTask.CompletedTask : new ValueTask(Release);
        }
    }
}
