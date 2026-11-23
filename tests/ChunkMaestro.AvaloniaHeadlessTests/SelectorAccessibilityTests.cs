using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Headless.XUnit;
using ChunkMaestro.Desktop;
using ChunkMaestro.Domain;
using Xunit;

namespace ChunkMaestro.AvaloniaHeadlessTests;

public sealed class SelectorAccessibilityTests
{
    [AvaloniaFact]
    public void CollapsedMetadataSelectionExposesLabelsInsteadOfRecordInternals()
    {
        Fleet fleet = new(new FleetId(Guid.NewGuid()), "Accessible fleet");
        Host host = new(new HostId(Guid.NewGuid()), fleet.Id, "Accessible host", "example.invalid",
            DateTimeOffset.UtcNow, Observation.Unknown);
        MetadataSelector selector = new()
        {
            ItemsSource = new object[]
            {
                fleet,
                host
            },
            SelectedItem = fleet
        };
        IValueProvider value = Assert.IsAssignableFrom<IValueProvider>(ControlAutomationPeer.CreatePeerForElement(selector));
        Assert.Equal(fleet.Name, value.Value);
        selector.SelectedItem = host;
        Assert.Equal(host.Name, value.Value);
        selector.SelectedItem = null;
        Assert.Null(value.Value);
    }
}
