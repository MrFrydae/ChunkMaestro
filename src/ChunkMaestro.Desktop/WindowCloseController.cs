using Avalonia.Controls;
using Avalonia.Threading;
using ChunkMaestro.Presentation;

namespace ChunkMaestro.Desktop;

internal sealed class WindowCloseController
{
    private readonly MainWindow window;
    private readonly FleetViewModel model;
    private bool pending;
    private bool ready;

    public bool CleanupFailed
    {
        get;
        private set;
    }

    public WindowCloseController(MainWindow window, FleetViewModel model)
    {
        this.window = window;
        this.model = model;
        window.Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (ready || CleanupFailed)
        {
            return;
        }
        args.Cancel = true;
        if (pending)
        {
            return;
        }
        pending = true;
        window.IsEnabled = false;
        try
        {
            await model.DisposeAsync();
            ready = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
            });
        }
        catch (Exception)
        {
            CleanupFailed = true;
            window.IsEnabled = true;
            window.ShowCloseFailure();
        }
    }
}
