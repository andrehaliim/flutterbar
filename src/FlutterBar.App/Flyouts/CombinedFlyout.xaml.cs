using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FlutterBar.App.Widget;

namespace FlutterBar.App.Flyouts;

/// <summary>
/// Compact / ButtonsOnly: a row of tabs (project, branch, config, device) like the Full layout's chips. Picking a tab raises
/// <see cref="SectionChosen"/>; the controller then places that flyout below the tabs with <see cref="ShowSection"/>.
/// </summary>
internal sealed partial class CombinedFlyout : UserControl
{
    private readonly (ToggleButton Tab, WidgetChip Chip)[] _tabs;

    public CombinedFlyout()
    {
        InitializeComponent();
        ProjectTab.Tag = WidgetChip.Project;
        BranchTab.Tag = WidgetChip.Branch;
        ConfigTab.Tag = WidgetChip.Config;
        DeviceTab.Tag = WidgetChip.Device;
        _tabs = [(ProjectTab, WidgetChip.Project), (BranchTab, WidgetChip.Branch), (ConfigTab, WidgetChip.Config), (DeviceTab, WidgetChip.Device)];
    }

    public event Action<WidgetChip>? SectionChosen;

    /// <summary>Keyboard focus when no section is shown.</summary>
    public IInputElement FocusTarget => ProjectTab;

    /// <summary>The flyout shown below the tabs, or null.</summary>
    public FrameworkElement? CurrentSection => Section.Content as FrameworkElement;

    public void SetValues(string project, string? branch, string config, bool configHasError, string device)
    {
        SetTab(ProjectTab, project, "Project");
        BranchTab.Visibility = branch is null ? Visibility.Collapsed : Visibility.Visible;
        SetTab(BranchTab, branch is null ? string.Empty : "⎇ " + branch, "Branch");
        SetTab(ConfigTab, config, "Config");
        if (configHasError)
        {
            ConfigTab.SetResourceReference(ForegroundProperty, "Fb.Red");
        }
        else
        {
            ConfigTab.ClearValue(ForegroundProperty);
        }
        SetTab(DeviceTab, device, "Device");
    }

    /// <summary>
    /// Shows <paramref name="content"/> below the tabs with <paramref name="chip"/>'s tab marked; null removes the section
    /// (it must be detached before the same flyout is shown on its own in the popup).
    /// </summary>
    public void ShowSection(WidgetChip? chip, FrameworkElement? content)
    {
        Section.Content = content;
        var visibility = content is null ? Visibility.Collapsed : Visibility.Visible;
        Section.Visibility = visibility;
        SectionSeparator.Visibility = visibility;
        foreach (var (tab, tabChip) in _tabs)
        {
            tab.IsChecked = content is not null && tabChip == chip;
        }
    }

    private static void SetTab(ToggleButton tab, string value, string label)
    {
        tab.Content = value;
        tab.ToolTip = $"{label}: {value}";
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WidgetChip chip })
        {
            SectionChosen?.Invoke(chip);
        }
    }
}
