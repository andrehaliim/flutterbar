using FlutterBar.App.Diagnostics;
using FlutterBar.App.Docking;
using FlutterBar.App.Menu;
using FlutterBar.App.Projects;
using FlutterBar.App.Theme;
using FlutterBar.App.Widget;
using FlutterBar.Core.Integration;
using FlutterBar.Core.Layout;
using FlutterBar.Core.Orchestration;

namespace FlutterBar.App;

/// <summary>Right-click menu wiring (spec §7.4, Task 16). One menu per widget window (a recreated window gets a new one).</summary>
public partial class App
{
    private StartupState? _startup; // last Run-key state read (null = unknown)
    private bool _opacityChanged; // the slider moved since the menu opened: save when it closes

    private void WireMenu(TaskbarWidget widget, TaskbarDocking docking)
    {
        var menu = widget.Menu;
        menu.DragToggled += on => docking.DragMode = on;
        menu.SizeChosen += size => ChangeSize(widget, docking, size);
        menu.StartupToggled += on => Observe(ToggleStartupAsync(menu, on), "Start with Windows");
        menu.CommandInvoked += command => OnMenuCommand(command, docking);
        menu.Opened += (_, _) => OnMenuOpened(menu);
        menu.BackgroundOpacityChanged += ApplyBackgroundOpacity;
        menu.Closed += (_, _) => SaveBackgroundOpacity();
        menu.ThemeChosen += ChangeTheme;
    }

    private void OnMenuOpened(WidgetContextMenu menu)
    {
        menu.SetState(WidgetSizeNames.Parse(_settings.Widget.Size), _controller?.CurrentProject is not null);
        menu.SetBackgroundOpacity(_settings.Widget.BackgroundOpacity);
        menu.SetTheme(TaskbarTheme.ParseMode(_settings.Widget.Theme));
        ApplyStartup(menu);
        Observe(RefreshStartupAsync(menu), "Start with Windows"); // cached state first, the registry read follows
    }

    private void OnMenuCommand(MenuCommand command, TaskbarDocking docking)
    {
        if (_exiting || _controller is not { } controller)
        {
            return;
        }
        AppLog.Write($"menu: {command}");
        switch (command)
        {
            case MenuCommand.ResetPosition:
                ResetPosition(docking);
                break;
            case MenuCommand.ScanRoots:
                controller.OpenManageProjects(ManageProjectsSection.Roots);
                break;
            case MenuCommand.AddProject:
                controller.Observe(controller.AddProjectFromMenuAsync(), "Add project");
                break;
            case MenuCommand.HiddenProjects:
                controller.OpenManageProjects(ManageProjectsSection.Hidden);
                break;
            case MenuCommand.Rescan:
                controller.Observe(controller.RescanAsync(), "Rescan");
                break;
            case MenuCommand.Import:
                _importFlow?.Open(controller.CurrentProject);
                break;
            case MenuCommand.OpenLaunchJson:
                controller.Observe(controller.OpenLaunchJsonFromMenuAsync(), "Open launch.json");
                break;
            case MenuCommand.OpenLog:
                OpenLog();
                break;
            case MenuCommand.FlutterTools:
                controller.OpenFlutterToolsFromMenu();
                break;
            case MenuCommand.OpenTerminal:
                controller.Observe(controller.OpenTerminalAsync(), "Open terminal");
                break;
            case MenuCommand.AddToPath:
                if (_system is { } system)
                {
                    controller.Observe(system.AddCliToPathAsync(), "Add CLI to PATH");
                }
                break;
            case MenuCommand.Exit:
                controller.Observe(RequestExitAsync(), "Exit");
                break;
        }
    }

    /// <summary>Size menu: <c>widget.size</c> changes (<see cref="WidgetSizeMenu.Apply"/>).</summary>
    private void ChangeSize(TaskbarWidget widget, TaskbarDocking docking, WidgetSize size)
    {
        if (!WidgetSizeMenu.Apply(_settings.Widget, size))
        {
            return;
        }
        widget.PreferredSize = size;
        docking.PreferredSize = size; // re-evaluates the placement
        docking.InvalidateLayout();
        SaveSettings($"size={_settings.Widget.Size}");
    }

    /// <summary>Background opacity slider (spec §7.6): applied live while dragging, saved once when the menu closes.</summary>
    private void ApplyBackgroundOpacity(int percent)
    {
        _settings.Widget.BackgroundOpacity = percent; // clamped by the setting
        if (_viewModel is { } vm)
        {
            vm.BackgroundOpacity = _settings.Widget.BackgroundOpacity / 100.0;
        }
        _opacityChanged = true;
    }

    /// <summary>Style ▸ Theme (spec §7.6): every FlutterBar window switches at once (DynamicResource brushes), then saved.</summary>
    private void ChangeTheme(ThemeChoice mode)
    {
        var setting = TaskbarTheme.ToSetting(mode);
        if (setting == _settings.Widget.Theme)
        {
            return;
        }
        _settings.Widget.Theme = setting;
        if (_theme is { } theme)
        {
            theme.Mode = mode;
        }
        SaveSettings($"theme={setting}");
    }

    private void SaveBackgroundOpacity()
    {
        if (_opacityChanged)
        {
            _opacityChanged = false;
            SaveSettings($"backgroundOpacity={_settings.Widget.BackgroundOpacity}");
        }
    }

    /// <summary>"Exit": with an active session the user confirms "Stop the session and exit?" first; then stop and exit.</summary>
    private async Task RequestExitAsync()
    {
        if (_exiting)
        {
            return;
        }
        if (_session is { } session && _controller is { } controller && ExitFlow.NeedsConfirmation(session.Snapshot.State))
        {
            var yes = await controller.ConfirmAsync(UiText.ConfirmExit, UiText.ConfirmExitYes, UiText.ConfirmNo);
            AppLog.Write($"exit with an active session ({session.Snapshot.State}): {(yes ? "stop and exit" : "cancelled")}");
            if (!yes)
            {
                return;
            }
        }
        await ExitAsync();
    }

    private async Task ToggleStartupAsync(WidgetContextMenu menu, bool on)
    {
        if (_system is not { } system)
        {
            return;
        }
        _startup = await system.SetStartupAsync(on);
        MirrorStartupSetting();
        ApplyStartup(menu);
    }

    /// <summary>Reads the Run key (off the UI thread) and updates the menu check mark, if a menu is given.</summary>
    private async Task RefreshStartupAsync(WidgetContextMenu? menu)
    {
        if (_system is not { } system)
        {
            return;
        }
        _startup = await system.ReadStartupAsync();
        MirrorStartupSetting();
        if (menu is not null)
        {
            ApplyStartup(menu);
        }
    }

    private void ApplyStartup(WidgetContextMenu menu) => menu.SetStartup(
        _startup is null ? null : _startup != StartupState.Off,
        _startup switch
        {
            null => "Couldn't read the registry status (see diag.log)",
            StartupState.OtherPath => "Registered for FlutterBar.App.exe in another folder: turn it off and on again to use this exe",
            _ => null,
        });

    // settings.startWithWindows mirrors the registry, which stays the source of truth.
    private void MirrorStartupSetting()
    {
        if (_startup is { } state && _settings.StartWithWindows != (state != StartupState.Off))
        {
            _settings.StartWithWindows = state != StartupState.Off;
            SaveSettings($"startWithWindows={_settings.StartWithWindows}");
        }
    }

#if DEBUG
    /// <summary>
    /// Debug-only switches for automated checks without a mouse: <c>--open-import &lt;projectDir&gt;</c> opens the import
    /// dialog for any folder (the current project in settings.json is not changed); <c>--open-menu-after &lt;seconds&gt;</c>
    /// opens the right-click menu once, so UI Automation can expand/invoke its items.
    /// </summary>
    /// <remarks>
    /// Also <c>--fail-startup</c> (throws at the end of startup, after the widget and the IPC server exist) and
    /// <c>--fail-startup-late</c> (throws from a dispatcher callback before the first idle) to check that a broken startup
    /// releases the single-instance lock and exits with code 1.
    /// </remarks>
    private void ApplyDebugMenuSwitches(string[] args)
    {
        if (args.Contains("--fail-startup-late"))
        {
            Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("debug: --fail-startup-late")), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        if (args.Contains("--fail-startup"))
        {
            throw new InvalidOperationException("debug: --fail-startup");
        }
        var import = Array.IndexOf(args, "--open-import");
        if (import >= 0 && import + 1 < args.Length)
        {
            var root = args[import + 1];
            Dispatcher.BeginInvoke(() => _importFlow?.Open(root), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        var menu = Array.IndexOf(args, "--open-menu-after");
        if (menu >= 0 && menu + 1 < args.Length && double.TryParse(args[menu + 1], System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            _ = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(seconds), System.Windows.Threading.DispatcherPriority.Normal, (t, _) =>
            {
                ((System.Windows.Threading.DispatcherTimer)t!).Stop();
                if (_widget is { } widget)
                {
                    AppLog.Write("debug: opening the right-click menu (--open-menu-after)");
                    widget.Menu.PlacementTarget = widget;
                    widget.Menu.IsOpen = true;
                }
            }, Dispatcher);
        }
    }
#endif

    private void Observe(Task task, string what)
    {
        if (_controller is { } controller)
        {
            controller.Observe(task, what);
        }
        else
        {
            _ = task.ContinueWith(t => AppLog.Write($"{what} failed", t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
