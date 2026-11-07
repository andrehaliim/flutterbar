using System.Windows;
using System.Windows.Controls;
using FlutterBar.App.Widget;

namespace FlutterBar.App.Flyouts;

/// <summary>Compact / ButtonsOnly: one row per dropdown; picking a row opens that flyout in the same popup.</summary>
internal sealed partial class CombinedFlyout : UserControl
{
    private readonly FlyoutList _list;

    public CombinedFlyout()
    {
        InitializeComponent();
        _list = new FlyoutList(List, null);
        _list.Chosen += item =>
        {
            if (item.Value is WidgetChip chip)
            {
                SectionChosen?.Invoke(chip);
            }
        };
    }

    public event Action<WidgetChip>? SectionChosen;

    public IInputElement FocusTarget => List;

    public void SetValues(string project, string? branch, string config, string device)
    {
        var items = new List<FlyoutItem> { Row("Project", project, WidgetChip.Project) };
        if (branch is not null)
        {
            items.Add(Row("Branch", branch, WidgetChip.Branch));
        }
        items.Add(Row("Config", config, WidgetChip.Config));
        items.Add(Row("Device", device, WidgetChip.Device));
        _list.SetItems(items);
    }

    private static FlyoutItem Row(string label, string value, WidgetChip chip) => new() { Title = value, Detail = label, Value = chip };
}
