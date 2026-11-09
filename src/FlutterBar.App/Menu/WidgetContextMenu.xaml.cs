using System.Windows;
using System.Windows.Controls;
using FlutterBar.Core.Layout;

namespace FlutterBar.App.Menu;

internal enum MenuCommand
{
    ResetPosition,
    ScanRoots,
    AddProject,
    HiddenProjects,
    Rescan,
    Import,
    OpenLaunchJson,
    OpenLog,
    FlutterTools,
    OpenTerminal,
    AddToPath,
    Exit,
}

/// <summary>
/// The widget's right-click menu (spec §7.4). It only raises events; the app does the work. Commands are dispatched after
/// the menu has closed (BeginInvoke), so dialogs and toasts never open underneath a closing menu.
/// </summary>
internal sealed partial class WidgetContextMenu : ContextMenu
{
    public WidgetContextMenu()
    {
        InitializeComponent();
        SizeFull.Tag = WidgetSize.Full;
        SizeCompact.Tag = WidgetSize.Compact;
        SizeButtons.Tag = WidgetSize.ButtonsOnly;
    }

    public event Action<MenuCommand>? CommandInvoked;

    public event Action<WidgetSize>? SizeChosen;

    public event Action<bool>? DragToggled;

    public event Action<bool>? StartupToggled;

    public bool DragChecked
    {
        get => DragItem.IsChecked;
        set => DragItem.IsChecked = value;
    }

    /// <summary>Called when the menu opens: preferred size check mark, project-dependent items.</summary>
    public void SetState(WidgetSize preferredSize, bool hasProject)
    {
        foreach (var item in new[] { SizeFull, SizeCompact, SizeButtons })
        {
            item.IsChecked = (WidgetSize)item.Tag == preferredSize;
        }
        ImportItem.IsEnabled = hasProject;
        LaunchJsonItem.IsEnabled = hasProject;
        FlutterToolsItem.IsEnabled = hasProject;
        TerminalItem.IsEnabled = hasProject;
    }

    /// <summary>Start with Windows as the registry says: null = unknown (unreadable), <paramref name="toolTip"/> explains odd states.</summary>
    public void SetStartup(bool? on, string? toolTip)
    {
        StartupItem.IsChecked = on == true;
        StartupItem.ToolTip = toolTip;
    }

    private void Raise(MenuCommand command) => Dispatcher.BeginInvoke(() => CommandInvoked?.Invoke(command));

    private void OnSizeClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: WidgetSize size })
        {
            Dispatcher.BeginInvoke(() => SizeChosen?.Invoke(size));
        }
    }

    // Synchronous, exactly as the Task 13 menu did (the docking's drag mode does not open anything).
    private void OnDragClick(object sender, RoutedEventArgs e) => DragToggled?.Invoke(DragItem.IsChecked);

    private void OnStartupClick(object sender, RoutedEventArgs e)
    {
        var on = StartupItem.IsChecked;
        Dispatcher.BeginInvoke(() => StartupToggled?.Invoke(on));
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.ResetPosition);

    private void OnScanRootsClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.ScanRoots);

    private void OnAddProjectClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.AddProject);

    private void OnHiddenProjectsClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.HiddenProjects);

    private void OnRescanClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.Rescan);

    private void OnImportClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.Import);

    private void OnOpenLaunchJsonClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.OpenLaunchJson);

    private void OnOpenLogClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.OpenLog);

    private void OnFlutterToolsClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.FlutterTools);

    private void OnOpenTerminalClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.OpenTerminal);

    private void OnAddToPathClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.AddToPath);

    private void OnExitClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.Exit);
}
