using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Docking;
using FlutterBar.App.Import;
using FlutterBar.App.Log;
using FlutterBar.App.Services;
using FlutterBar.App.SystemIntegration;
using FlutterBar.App.Theme;
using FlutterBar.App.Widget;
using FlutterBar.Core.Diagnostics;
using FlutterBar.Core.Integration;
using FlutterBar.Core.Ipc;
using FlutterBar.Core.Layout;
using FlutterBar.Core.Logging;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Settings;

namespace FlutterBar.App;

/// <summary>
/// Composition root: settings, theme, run session, the widget window and its docking (Task 13), and the
/// <see cref="AppController"/> that wires flyouts, toasts, Run and the IPC server (Task 14), the LogWindow (Task 15), and
/// (Task 16) single instance, global exception handlers, the right-click menu (<c>App.Menu.cs</c>), the IDE import and the
/// PATH / Start-with-Windows integration.
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
    private ImportFlow? _importFlow;
    private SystemActions? _system;
    private SingleInstanceLock? _instance;
    private bool _exiting;
    private bool _startupCompleted;
    private bool _startupFailed;
    private DateTime _lastErrorToast = DateTime.MinValue;

    private static string DiagPath => Path.Combine(Path.GetDirectoryName(AppSettingsStore.DefaultPath) ?? ".", "diag.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DiagLog.Initialize(DiagPath);
        InstallGlobalHandlers();

        // Before anything else, and before the IPC server can start: only the mutex owner may listen on the pipe.
        _instance = SingleInstanceLock.TryAcquire(SingleInstanceLock.NameForCurrentUser(), out var abandoned);
        if (_instance is null)
        {
            AppLog.Write($"second instance pid={Environment.ProcessId}: FlutterBar already runs, asking it to show the widget");
            _ = HandOverToFirstInstanceAsync();
            return;
        }
        AppLog.Write($"FlutterBar.App start pid={Environment.ProcessId} 64bit={Environment.Is64BitProcess} os={Environment.OSVersion.Version}");
        if (abandoned)
        {
            AppLog.Write("single instance: abandoned mutex taken over (the previous instance ended without releasing it)");
        }

        // We hold the mutex now: a startup failure must end the process, or a windowless process would keep the mutex
        // and every later launch would hand over to it and exit (e.g. logon start while Explorer is still coming up).
        try
        {
            StartFirstInstance(e.Args);
        }
        catch (Exception ex) // top of startup: logged, mutex released, process ends with exit code 1
        {
            FailStartup("startup failed after taking the single-instance lock", ex);
            return;
        }
        // Until the first idle (window shown, first placement done) an unhandled UI exception still counts as startup.
        Dispatcher.BeginInvoke(() =>
        {
            if (!_startupFailed)
            {
                _startupCompleted = true;
                AppLog.Write("startup completed");
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void StartFirstInstance(string[] args)
    {
        _settings = LoadSettings(out var warning); // before any window exists, so the synchronous read cannot stall the taskbar
        _saver = new SettingsSaver(_store, _settings);
        _theme = new TaskbarTheme(Resources, Dispatcher);
        _session = new RunSession(new ProcessLauncher(), _log);
        _viewModel = new WidgetViewModel(_session, Dispatcher);
        LogWindow.Configure(_log);
        _viewModel.OpenLogRequested += OpenLog;
        _controller = new AppController(Dispatcher, _saver, _session, _log, _viewModel, () => _widget, ShowWidgetFromCli, warning);
        _importFlow = new ImportFlow(_controller);
        _controller.ImportRequested += _importFlow.Open; // "No config yet → Import from IDE"
        _system = new SystemActions(_controller, new WindowsUserRegistry());
        CreateWidget("startup");
        _ = InitializeControllerAsync(_controller);
        _controller.Observe(RefreshStartupAsync(null), "Start with Windows");
#if DEBUG
        // Debug-only switches for checking the LogWindow while the widget is hidden (e.g. fullscreen).
        if (args.Contains("--fill-log"))
        {
            DebugLogFiller.Fill(_log, 20_000);
        }
        DebugLogFiller.GcOnLogClose = args.Contains("--gc-on-log-close");
        if (args.Contains("--stream-log"))
        {
            DebugLogFiller.StreamInBackground(_log, 20);
        }
        if (args.Contains("--open-log"))
        {
            Dispatcher.BeginInvoke(() => LogWindow.ShowAt(null));
        }
        if (args.Contains("--close-log-after-6s"))
        {
            var closeTimer = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Normal, (t, _) =>
            {
                ((DispatcherTimer)t!).Stop();
                LogWindow.CloseIfOpen();
            }, Dispatcher);
        }
        ApplyDebugMenuSwitches(args);
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
            warning = $"Couldn't read settings.json, using defaults: {ex.Message}";
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
            controller.ShowToast($"FlutterBar failed to start: {ex.Message}");
        }
    }

    /// <summary>
    /// Second instance: ask the running one to show its widget (IPC <c>show</c>), then exit. No window exists here. The
    /// first instance may still be starting (pipe not listening yet): retried for up to ~3 s.
    /// </summary>
    private async Task HandOverToFirstInstanceAsync()
    {
        try
        {
            var client = new IpcClient(PipeNames.ForCurrentUser());
            var (response, attempts) = await Task.Run(() =>
                StartupGuard.RequestShowAsync(client, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(250), CancellationToken.None));
            AppLog.Write(response is null
                ? $"second instance: the running instance's pipe was not reachable after {attempts} attempt(s), exiting"
                : $"second instance: show -> exit {response.ExitCode} {response.Message} (attempt {attempts})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Write("second instance: show request failed, exiting", ex);
        }
        finally
        {
            AppLog.Flush(TimeSpan.FromSeconds(1));
            Shutdown();
        }
    }

    // IPC "show" (any thread): second launch or the CLI. Visibility belongs to the docking (fullscreen / auto-hide):
    // a hidden widget stays hidden (logged); a visible one is re-placed and flashes once.
    private void ShowWidgetFromCli() => Dispatcher.BeginInvoke(() =>
    {
        var hidden = _viewModel?.IsHidden == true;
        AppLog.Write(hidden
            ? "show requested: widget hidden by fullscreen or auto-hide, left hidden"
            : "show requested: widget re-placed and flashed");
        _docking?.InvalidateLayout();
        if (!hidden)
        {
            _widget?.Flash();
        }
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

        WireMenu(widget, docking);
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
        try
        {
            await TearDownAsync();
        }
        catch (Exception ex) // a failing step must not leave a widget whose every command (Exit included) is a no-op
        {
            AppLog.Write("exit: teardown failed, shutting down anyway", ex);
        }
        finally
        {
            AppLog.Write("FlutterBar.App exit");
            AppLog.Flush(TimeSpan.FromSeconds(1)); // window is gone (or the exit is forced): nothing left to stall
            _instance?.Dispose(); // on the UI thread, which acquired it
            Shutdown();
        }
    }

    private async Task TearDownAsync()
    {
        if (_controller is { } controller)
        {
            await controller.DisposeAsync();
        }
        if (_session is { } session)
        {
            if (session.Snapshot.State is not (SessionState.Idle or SessionState.Failed))
            {
                await session.StopAsync(); // confirmed in RequestExitAsync; the Job Object kills whatever remains
            }
            await session.DisposeAsync(); // also closes the jobs kept warm by earlier stops (Gradle, adb)
        }
        LogWindow.CloseIfOpen();
        _importFlow?.CloseIfOpen();
        _controller?.CloseDialogs();
        _docking?.Stop();
        _widget?.Close();
        _viewModel?.Dispose();
        _theme?.Dispose();
        if (_saver is { } saver)
        {
            await saver.FlushAsync();
        }
    }

    /// <summary>
    /// Startup broke (before the first idle): log synchronously, release the single-instance mutex so the next launch
    /// starts normally, and end the process with exit code 1. Shutdown closes any window already created.
    /// </summary>
    private void FailStartup(string what, Exception ex)
    {
        if (_startupFailed)
        {
            return;
        }
        _startupFailed = true;
        AppLog.Flush(TimeSpan.FromSeconds(1)); // earlier entries first; later AppLog writes go straight to diag.log
        DiagLog.Write($"{what}: releasing the single-instance lock and exiting with code {StartupGuard.FailedExitCode}", ex);
        _instance?.Dispose();
        _instance = null;
        Shutdown(StartupGuard.FailedExitCode);
    }

    private void InstallGlobalHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Terminating: write synchronously, AppLog's queue would never be flushed.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagLog.Write($"unhandled exception (terminating={args.IsTerminating}): {args.ExceptionObject.GetType().FullName}", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true; // never the default crash dialog; during startup the process ends below instead
        if (StartupGuard.OnUnhandledUiException(_startupCompleted) == UnhandledAction.ExitProcess)
        {
            FailStartup("unhandled UI exception during startup", e.Exception);
            return;
        }
        AppLog.Write("unhandled UI exception (kept running)", e.Exception);
        var now = DateTime.UtcNow;
        if (now - _lastErrorToast < TimeSpan.FromSeconds(10) || _controller is not { } controller)
        {
            return; // no toast storm: at most one per 10 s
        }
        _lastErrorToast = now;
        try
        {
            controller.ShowToast(UiText.UnexpectedError);
        }
        catch (Exception ex) // the handler itself must not throw (that would end the process)
        {
            AppLog.Write("error toast could not be shown", ex);
        }
    }
}
