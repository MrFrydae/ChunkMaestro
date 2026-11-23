using Avalonia.Controls;
using Avalonia.Interactivity;
using ChunkMaestro.Presentation;

namespace ChunkMaestro.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
    private FleetViewModel Model => (FleetViewModel) DataContext!;
    private async void OpenClick(object? sender, RoutedEventArgs args) => await Model.OpenAsync();
    private async void ReloadClick(object? sender, RoutedEventArgs args) => await Model.ReloadAsync();
    private async void FleetClick(object? sender, RoutedEventArgs args) => await Model.RegisterFleetAsync();
    private async void HostClick(object? sender, RoutedEventArgs args) => await Model.RegisterHostAsync();
    private async void InstanceClick(object? sender, RoutedEventArgs args) => await Model.RegisterInstanceAsync();
}
