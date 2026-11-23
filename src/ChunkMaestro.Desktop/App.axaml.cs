using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChunkMaestro.Application;
using ChunkMaestro.Persistence;
using ChunkMaestro.Presentation;

namespace ChunkMaestro.Desktop;

public sealed partial class App : Avalonia.Application
{
    private readonly Func<bool, Task<IFleetInventory>> open;
    private readonly string? storagePath;

    public App() : this(async confirmed => await LocalFleetStore.OpenAsync(StorageLocation.ForCurrentUser(confirmed)))
    {
    }

    public App(Func<bool, Task<IFleetInventory>> open, string? storagePath = null)
    {
        this.open = open ?? throw new ArgumentNullException(nameof(open));
        this.storagePath = storagePath;
        Name = "ChunkMaestro";
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            FleetViewModel model = new(open, storagePath);
            MainWindow window = new()
            {
                DataContext = model
            };
            WindowCloseController close = new(window, model);
            desktop.Exit += (_, args) =>
            {
                if (close.CleanupFailed)
                {
                    args.ApplicationExitCode = 1;
                }
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
