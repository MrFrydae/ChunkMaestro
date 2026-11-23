using Avalonia.Automation.Peers;
using Avalonia.Controls;
using ChunkMaestro.Domain;

namespace ChunkMaestro.Desktop;

public sealed class MetadataSelector : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new MetadataSelectorPeer(this);
    }

    private sealed class MetadataSelectorPeer : ComboBoxAutomationPeer
    {
        private readonly SelectedMetadataPeer selection;

        public MetadataSelectorPeer(MetadataSelector owner) : base(owner)
        {
            selection = new SelectedMetadataPeer(this);
        }

        protected override IReadOnlyList<AutomationPeer>? GetSelectionCore()
        {
            if (Owner.IsDropDownOpen)
            {
                return base.GetSelectionCore();
            }
            return Owner.SelectedItem is null ? null : [selection];
        }
    }

    private sealed class SelectedMetadataPeer(MetadataSelectorPeer parent) : UnrealizedElementAutomationPeer
    {
        protected override string? GetAcceleratorKeyCore() => null;
        protected override string? GetAccessKeyCore() => null;
        protected override string? GetAutomationIdCore() => null;
        protected override string GetClassNameCore() => nameof(ComboBoxItem);
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override AutomationPeer GetParentCore() => parent;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

        protected override string GetNameCore()
        {
            return parent.Owner.SelectedItem switch
            {
                Fleet fleet => fleet.Name,
                Host host => host.Name,
                _ => string.Empty
            };
        }
    }
}
