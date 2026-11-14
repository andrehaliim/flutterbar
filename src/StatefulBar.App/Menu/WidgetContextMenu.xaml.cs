using System.Windows;
using System.Windows.Controls;
using StatefulBar.App.Theme;
using StatefulBar.Core.Docking;
using StatefulBar.Core.Layout;
using StatefulBar.Core.Orchestration;

namespace StatefulBar.App.Menu;

internal enum MenuCommand
{
    ResetPosition,
    ScanRoots,
    AddProject,
    HiddenProjects,
    Rescan,
    Import,
    EditLaunchJson,
    OpenLog,
    FlutterTools,
    OpenTerminal,
    CheckForUpdates,
    AddToPath,
    StartupSettings,
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
        ThemeSystem.Tag = ThemeChoice.System;
        ThemeLight.Tag = ThemeChoice.Light;
        ThemeDark.Tag = ThemeChoice.Dark;
        MenuDismissal.Attach(this); // a click outside (desktop, another app) closes the menu
    }

    public event Action<MenuCommand>? CommandInvoked;

    public event Action<WidgetSize>? SizeChosen;

    public event Action<bool>? DragToggled;

    public event Action<bool>? StartupToggled;

    /// <summary>The background opacity slider moved (percent), raised live; not raised by <see cref="SetBackgroundOpacity"/>.</summary>
    public event Action<int>? BackgroundOpacityChanged;

    private bool _settingOpacity;

    /// <summary>Called when the menu opens: the stored <c>widget.backgroundOpacity</c>.</summary>
    public void SetBackgroundOpacity(int percent)
    {
        _settingOpacity = true;
        try
        {
            OpacitySlider.Value = percent;
            OpacityValue.Text = $"{percent}%";
        }
        finally
        {
            _settingOpacity = false;
        }
    }

    /// <summary>Show on ▸ a taskbar was picked: its <c>widget.monitor</c> value (null = main taskbar).</summary>
    public event Action<string?>? MonitorChosen;

    /// <summary>Called when the menu opens: one entry per taskbar (check mark on the current one); disabled with a single taskbar.</summary>
    public void SetMonitors(IReadOnlyList<TaskbarMenuItem> taskbars)
    {
        ShowOnItem.Items.Clear();
        foreach (var taskbar in taskbars)
        {
            var item = new MenuItem { Header = taskbar.Label, IsChecked = taskbar.IsCurrent, Tag = taskbar };
            item.Click += OnMonitorClick;
            ShowOnItem.Items.Add(item);
        }
        ShowOnItem.IsEnabled = taskbars.Count > 1;
        ShowOnItem.ToolTip = taskbars.Count > 1 ? null : "Only one taskbar: turn on \"Show my taskbar on all displays\" in Windows";
        ToolTipService.SetShowOnDisabled(ShowOnItem, true);
    }

    /// <summary>Style ▸ Theme: System, Light or Dark was picked.</summary>
    public event Action<ThemeChoice>? ThemeChosen;

    /// <summary>Called when the menu opens (and after a pick): check mark on the current theme mode.</summary>
    public void SetTheme(ThemeChoice mode)
    {
        foreach (var item in new[] { ThemeSystem, ThemeLight, ThemeDark })
        {
            item.IsChecked = (ThemeChoice)item.Tag == mode;
        }
    }

    public bool DragChecked
    {
        get => DragItem.IsChecked;
        set => DragItem.IsChecked = value;
    }

    /// <summary>
    /// Called when the menu opens: preferred size check mark (kept on a size that does not fit: auto mode goes back to it
    /// once there is room), sizes without room on the taskbar disabled with a note, project-dependent items.
    /// </summary>
    public void SetState(WidgetSize preferredSize, IReadOnlySet<WidgetSize> sizesThatFit, bool hasProject)
    {
        foreach (var item in new[] { SizeFull, SizeCompact, SizeButtons })
        {
            var size = (WidgetSize)item.Tag;
            var fits = sizesThatFit.Contains(size);
            item.IsChecked = size == preferredSize;
            item.IsEnabled = fits;
            item.ToolTip = fits ? null : WidgetSizeMenu.NoRoomToolTip;
            ToolTipService.SetShowOnDisabled(item, true);
        }
        var note = WidgetSizeMenu.NoRoomNote(sizesThatFit.ToList());
        SizeNote.Text = note ?? string.Empty;
        SizeNoteSeparator.Visibility = SizeNoteItem.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        ImportItem.IsEnabled = hasProject;
        LaunchJsonItem.IsEnabled = hasProject;
        FlutterToolsItem.IsEnabled = hasProject;
        TerminalItem.IsEnabled = hasProject;
    }

    /// <summary>Updates ▸ Check automatically was switched on or off.</summary>
    public event Action<bool>? AutoUpdateToggled;

    /// <summary>Called when the menu opens: the stored <c>updates.auto</c>.</summary>
    public void SetAutoUpdate(bool on) => AutoUpdateItem.IsChecked = on;

    private bool _startupIsToggle = true;

    /// <summary>
    /// Called once per menu: how "Add CLI to PATH…" and "Start with Windows" behave for this process (plain exe or
    /// package, <see cref="SystemMenu"/>).
    /// </summary>
    public void SetSystemMenu(SystemMenu system)
    {
        UpdatesItem.Visibility = system.ShowUpdates ? Visibility.Visible : Visibility.Collapsed;
        AddToPathItem.Visibility = system.ShowAddCliToPath ? Visibility.Visible : Visibility.Collapsed;
        _startupIsToggle = system.StartupIsToggle;
        StartupItem.IsCheckable = system.StartupIsToggle;
        StartupItem.Header = system.StartupHeader;
        if (!system.StartupIsToggle)
        {
            StartupItem.IsChecked = false;
            StartupItem.ToolTip = system.StartupToolTip;
        }
    }

    /// <summary>
    /// Start with Windows as the registry says: null = unknown (unreadable), <paramref name="toolTip"/> explains odd
    /// states. Ignored when the item is a command (<see cref="SetSystemMenu"/>).
    /// </summary>
    public void SetStartup(bool? on, string? toolTip)
    {
        if (!_startupIsToggle)
        {
            return;
        }
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

    private void OnMonitorClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: TaskbarMenuItem taskbar })
        {
            Dispatcher.BeginInvoke(() => MonitorChosen?.Invoke(taskbar.Value)); // after the menu closed, like the commands
        }
    }

    // Synchronous like the slider: the colours switch at once (the click closes the menu).
    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ThemeChoice mode })
        {
            SetTheme(mode);
            ThemeChosen?.Invoke(mode);
        }
    }

    // Raised during InitializeComponent too (Value coerced by Minimum): the label may not exist yet, nobody listens yet.
    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var percent = (int)Math.Round(e.NewValue);
        if (OpacityValue is not null)
        {
            OpacityValue.Text = $"{percent}%";
        }
        if (!_settingOpacity)
        {
            BackgroundOpacityChanged?.Invoke(percent);
        }
    }

    // Synchronous, exactly as the Task 13 menu did (the docking's drag mode does not open anything).
    private void OnDragClick(object sender, RoutedEventArgs e) => DragToggled?.Invoke(DragItem.IsChecked);

    private void OnStartupClick(object sender, RoutedEventArgs e)
    {
        if (!_startupIsToggle)
        {
            Raise(MenuCommand.StartupSettings);
            return;
        }
        var on = StartupItem.IsChecked;
        Dispatcher.BeginInvoke(() => StartupToggled?.Invoke(on));
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.ResetPosition);

    private void OnScanRootsClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.ScanRoots);

    private void OnAddProjectClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.AddProject);

    private void OnHiddenProjectsClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.HiddenProjects);

    private void OnRescanClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.Rescan);

    private void OnImportClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.Import);

    private void OnEditLaunchJsonClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.EditLaunchJson);

    private void OnOpenLogClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.OpenLog);

    private void OnFlutterToolsClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.FlutterTools);

    private void OnOpenTerminalClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.OpenTerminal);

    private void OnCheckUpdatesClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.CheckForUpdates);

    private void OnAutoUpdateClick(object sender, RoutedEventArgs e)
    {
        var on = AutoUpdateItem.IsChecked;
        Dispatcher.BeginInvoke(() => AutoUpdateToggled?.Invoke(on));
    }

    private void OnAddToPathClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.AddToPath);

    private void OnExitClick(object sender, RoutedEventArgs e) => Raise(MenuCommand.Exit);
}
