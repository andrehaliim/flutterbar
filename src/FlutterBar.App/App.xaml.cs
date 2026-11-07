using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Docking;
using FlutterBar.App.Log;
using FlutterBar.App.Services;
using FlutterBar.App.Theme;
using FlutterBar.App.Widget;
using FlutterBar.Core.Layout;
using FlutterBar.Core.Logging;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Settings;

namespace FlutterBar.App;

/// <summary>
/// Composition root: settings, theme, run session, the widget window and its docking (Task 13), and the
/// <see cref="AppController"/> that wires flyouts, toasts, Run and the IPC server (Task 14). the LogWindow (Task 15),
/// Task 16 the full menu, the IDE import and single instance.
/// </summary>
public partial class App : Application
{
    private readonly AppSettingsStore _store = new(AppSettingsStore.DefaultPath);
    private readonly LogBuffer _log = new();
    private AppSettings _settings = new();
    private SettingsSaver? _saver;
    private TaskbarTheme? _theme;
    private RunSession? _session;
    private WidgetViewModel? _viewModel;
    private TaskbarWidget? _widget;
    private TaskbarDocking? _docking;
    private AppController? _controller;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Write("unhandled exception (terminating)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => AppLog.Write("unobserved task exception", args.Exception);
        AppLog.Write($"FlutterBar.App start pid={Environment.ProcessId} 64bit={Environment.Is64BitProcess} os={Environment.OSVersion.Version}");

        _settings = LoadSettings(out var warning); // before any window exists, so the synchronous read cannot stall the taskbar
        _saver = new SettingsSaver(_store, _settings);
        _theme = new TaskbarTheme(Resources, Dispatcher);
        _session = new RunSession(new ProcessLauncher(), _log);
        _viewModel = new WidgetViewModel(_session, Dispatcher);
        LogWindow.Configure(_log);
        _viewModel.OpenLogRequested += OpenLog;
        _controller = new AppController(Dispatcher, _saver, _session, _log, _viewModel, () => _widget, ShowWidgetFromCli, warning);
        CreateWidget("startup");
        _ = InitializeControllerAsync(_controller);
#if DEBUG
        // Debug-only switches for checking the LogWindow while the widget is hidden (e.g. fullscreen).
        if (e.Args.Contains("--fill-log"))
        {
            DebugLogFiller.Fill(_log, 20_000);
        }
        if (e.Args.Contains("--open-log"))
        {
            Dispatcher.BeginInvoke(() => LogWindow.ShowAt(null));
        }
#endif
    }

    // 🖥 button and the red status dot: open the log at the first error since the last reload (or at the end).
    private void OpenLog()
    {
        var firstError = _log.SinceLastReload().FirstOrDefault(l => l.Kind == LogKind.Error);
        LogWindow.ShowAt(firstError?.Seq);
    }

    private AppSettings LoadSettings(out string? warning)
    {
        warning = null;
        try
        {
            var settings = _store.Load(out warning);
            if (warning is not null)
            {
                AppLog.Write("settings: " + warning); // toast once the widget is up (AppController.InitializeAsync)
            }
            AppLog.Write($"settings: widget size={settings.Widget.Size} position={settings.Widget.Position} offsetFromRight={settings.Widget.OffsetFromRight?.ToString() ?? "-"} widthOverride={settings.Widget.WidthOverride?.ToString() ?? "-"}");
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("settings: load failed, using defaults", ex);
            warning = $"settings.json tidak bisa dibaca, memakai default: {ex.Message}";
            return new AppSettings();
        }
    }

    private static async Task InitializeControllerAsync(AppController controller)
    {
        try
        {
            await controller.InitializeAsync();
        }
        catch (Exception ex) // startup continuation: report, the widget itself keeps running
        {
            AppLog.Write("AppController initialization failed", ex);
            controller.ShowToast($"Inisialisasi FlutterBar gagal: {ex.Message}");
        }
    }

    // IPC "show" (any thread). Visibility belongs to the docking (fullscreen / auto-hide); re-evaluate placement now.
    private void ShowWidgetFromCli() => Dispatcher.BeginInvoke(() =>
    {
        AppLog.Write($"show requested via CLI (hidden={_viewModel?.IsHidden})");
        _docking?.InvalidateLayout();
    });

    /// <summary>Creates a widget window plus its docking; also used when the docking asks for a new window.</summary>
    private void CreateWidget(string why)
    {
        var vm = _viewModel ?? throw new InvalidOperationException("view model missing");
        var widget = new TaskbarWidget(vm)
        {
            PreferredSize = WidgetSizeNames.Parse(_settings.Widget.Size),
            WidthOverride = _settings.Widget.WidthOverride,
        };
        var docking = new TaskbarDocking(widget)
        {
            PreferredSize = widget.PreferredSize,
            RequiredWidths = widget.RequiredWidths,
        };

        widget.DragModeToggled += on => docking.DragMode = on;
        widget.ResetPositionRequested += () => ResetPosition(docking);
        widget.ExitRequested += () => _ = ExitAsync();
        widget.RequiredWidthsChanged += docking.InvalidateLayout;
        widget.LocationChanged += (_, _) => _controller?.OnWidgetMoved(); // open flyout/toast follow the widget
        widget.SizeChanged += (_, _) => _controller?.OnWidgetMoved();
        docking.DragModeChanged += widget.SetDragCue;
        docking.EffectiveSizeChanged += (size, full) =>
        {
            vm.EffectiveSize = size;
            vm.TaskbarFull = full;
        };
        docking.HiddenChanged += hidden => vm.IsHidden = hidden;
        docking.ManualOffsetChanged += SaveManualOffset;
        docking.WidthOverrideChanged += width =>
        {
            _settings.Widget.WidthOverride = width;
            widget.WidthOverride = _settings.Widget.WidthOverride; // validated value
            docking.InvalidateLayout();
            SaveSettings($"widthOverride={width}");
        };
        docking.RecreateRequested += reason => Dispatcher.BeginInvoke(() => Recreate(widget, reason));

        if (_settings.Widget.OffsetFromRight is { } offset)
        {
            docking.SetManual(offset);
        }
        docking.Start();
        widget.Show();
        _widget = widget;
        _docking = docking;
        AppLog.Write($"widget shown ({why}) inst={docking.InstanceId} preferred={widget.PreferredSize} manual={docking.IsManual}");
    }

    /// <summary>The old docking is already torn down; show the new window first, then close the old one.</summary>
    private void Recreate(TaskbarWidget old, string reason)
    {
        if (_exiting)
        {
            return;
        }
        var oldHandle = new System.Windows.Interop.WindowInteropHelper(old).Handle;
        _controller?.OnWidgetRecreating();
        CreateWidget("recreate: " + reason);
        if (oldHandle != IntPtr.Zero && Native.NativeMethods.IsWindow(oldHandle))
        {
            old.Close(); // its docking is already dead, so this does not trigger another recreate
        }
    }

    private void ResetPosition(TaskbarDocking docking)
    {
        docking.ResetToAuto();
        _settings.Widget.OffsetFromRight = null;
        _settings.Widget.Position = "auto";
        SaveSettings("reset to auto position");
    }

    private void SaveManualOffset(int offset)
    {
        _settings.Widget.OffsetFromRight = offset;
        _settings.Widget.Position = "manual";
        SaveSettings($"offsetFromRight={offset}");
    }

    private void SaveSettings(string what) => _saver?.Save(what);

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        AppLog.Write("exit requested from widget menu");
        if (_controller is { } controller)
        {
            await controller.DisposeAsync();
        }
        if (_session is { } session)
        {
            if (session.Snapshot.State is not (SessionState.Idle or SessionState.Failed))
            {
                await session.StopAsync(); // exit confirmation dialog arrives with Task 16
            }
            await session.DisposeAsync();
        }
        LogWindow.CloseIfOpen();
        _docking?.Stop();
        _widget?.Close();
        _viewModel?.Dispose();
        _theme?.Dispose();
        if (_saver is { } saver)
        {
            await saver.FlushAsync();
        }
        AppLog.Write("FlutterBar.App exit");
        AppLog.Flush(TimeSpan.FromSeconds(1)); // window is gone: nothing left to stall
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("unhandled UI exception (kept running)", e.Exception);
        e.Handled = true;
    }
}
