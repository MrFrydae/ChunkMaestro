using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ChunkMaestro.Application;
using ChunkMaestro.Desktop;
using ChunkMaestro.Presentation;
using Xunit;

namespace ChunkMaestro.AvaloniaHeadlessTests;

public sealed class RegistrationLabelTests
{
    [AvaloniaFact]
    public async Task PopulatedFieldsKeepVisibleLabelsAndDistinctProtocolNames()
    {
        await using FleetViewModel model = new(_ => Task.FromResult<IFleetInventory>(new MemoryInventory()))
        {
            LocalStorageConfirmed = true
        };
        await model.OpenAsync();
        MainWindow window = new()
        {
            DataContext = model
        };
        window.Show();
        try
        {
            foreach (string prefix in new string[]
            {
                "FleetName", "HostName", "HostAddress", "InstanceName", "ServiceName", "JavaPort", "BedrockPort"
            })
            {
                TextBox field = window.FindControl<TextBox>(prefix + "Input")!;
                field.Text = "25565";
                Label label = window.FindControl<Label>(prefix + "Label")!;
                Assert.True(label.IsVisible);
                Assert.Same(field, label.Target);
                Assert.False(string.IsNullOrWhiteSpace(label.Content?.ToString()));
            }
            Assert.Contains("Java TCP", window.FindControl<Label>("JavaPortLabel")!.Content!.ToString(), StringComparison.Ordinal);
            Assert.Contains("Bedrock UDP", window.FindControl<Label>("BedrockPortLabel")!.Content!.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }
}
