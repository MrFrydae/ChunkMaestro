using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChunkMaestro.Persistence;
using ChunkMaestro.Presentation;

namespace ChunkMaestro.Desktop;

public sealed partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            FleetViewModel model = new(async confirmed => await LocalFleetStore.OpenAsync(StorageLocation.ForCurrentUser(confirmed)));
            MainWindow window = new()
            {
                DataContext = model
            };
            bool closing = false;
            window.Closing += async (_, args) =>
            {
                if (closing)
                {
                    return;
                }

                args.Cancel = true;
                window.IsEnabled = false;
                await model.DisposeAsync();
                closing = true;
                window.Close();
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
