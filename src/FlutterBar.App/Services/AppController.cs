using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Flyouts;
using FlutterBar.App.Native;
using FlutterBar.App.Widget;
using FlutterBar.Core.Devices;
using FlutterBar.Core.Git;
using FlutterBar.Core.Ipc;
using FlutterBar.Core.Logging;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Settings;

namespace FlutterBar.App.Services;

/// <summary>
/// Orchestration point of the app (Task 14): wires the Core services to the widget, flyouts and toasts, runs the IPC
/// server and answers <see cref="IStatusProvider"/> for the CLI. Everything here runs on the UI thread; every git /
/// flutter / scan / file call is awaited off it (<see cref="Task.Run(Func{Task})"/>), because the widget is owned by
/// the taskbar and a stalled UI thread stalls taskbar input. Logic that needs no WPF lives in
/// <c>FlutterBar.Core.Orchestration</c> (unit tested); this class is glue.
/// </summary>
internal sealed partial class AppController : IStatusProvider, IAsyncDisposable
{
    private static readonly TimeSpan ReloadTimeout = TimeSpan.FromSeconds(60);

    private readonly Dispatcher _dispatcher;
    private readonly SettingsSaver _saver;
    private readonly RunSession _session;
    private readonly LogBuffer _log;
    private readonly WidgetViewModel _vm;
    private readonly Func<FrameworkElement?> _anchor;
    private readonly Action _showWidget;
    private readonly string? _settingsWarning;
    private readonly PathResolver _paths = new();
    private readonly ProcessRunner _runner = new();
    private readonly FlutterToolLocator _locator;
    private readonly Task<Tools> _tools;
    private readonly FlyoutHost _flyout;
    private readonly ToastHost _toast;
    private readonly CancellationTokenSource _lifetime = new();
    private StatusLabels _labels = new(null, null, null, null);
    private SessionState _lastState = SessionState.Idle;
    private Task _ipcTask = Task.CompletedTask;
    private bool _disposed;

    public AppController(
        Dispatcher dispatcher,
        SettingsSaver saver,
        RunSession session,
        LogBuffer log,
        WidgetViewModel vm,
        Func<FrameworkElement?> anchor,
        Action showWidget,
        string? settingsWarning)
    {
        _dispatcher = dispatcher;
        _saver = saver;
        _session = session;
        _log = log;
        _vm = vm;
        _anchor = anchor;
        _showWidget = showWidget;
        _settingsWarning = settingsWarning;
        _locator = new FlutterToolLocator(_paths, File.Exists);
        _tools = Task.Run(DiscoverTools); // PATH lookups touch the disk: never on the UI thread
        _flyout = new FlyoutHost(anchor, WidgetShown);
        _flyout.Closed += OnFlyoutClosed;
        _toast = new ToastHost(anchor, WidgetShown, dispatcher);
        vm.PropertyChanged += OnViewModelChanged;

        vm.RunRequested += () => Fire(RunAsync(), "Run");
        vm.ReloadRequested += full => Fire(ReloadAsync(full), full ? "Hot restart" : "Hot reload");
        vm.StopRequested += () => Fire(StopAsync(), "Stop");
        vm.ChipRequested += OpenFlyout;
        saver.SaveFailed += message => _dispatcher.BeginInvoke(() => ShowToast(message));
        session.Changed += OnSessionChanged;
    }

    /// <summary>
    /// "Import dari IDE" in the config flyout, with the project root. Task 16 handles it (import dialog); until then
    /// nobody subscribes and the request is only written to diag.log.
    /// </summary>
    public event Action<string>? ImportRequested;

    private AppSettings Settings => _saver.Settings;

    /// <summary>Restores the last selection, starts the background scan and the IPC server (brief Step 1).</summary>
    public async Task InitializeAsync()
    {
        if (_settingsWarning is not null)
        {
            ShowToast(_settingsWarning);
        }
        StartIpc();
        Fire(ScanAsync(), "Scan project");
        var tools = await _tools;
        if (tools.Git is null)
        {
            ShowToast("git tidak ditemukan di PATH: chip branch dinonaktifkan");
        }
        await ApplyProjectAsync(_saver.Settings.CurrentProject);
    }

    /// <summary>Shows a toast above the widget; <paramref name="action"/> runs when its button is pressed.</summary>
    public void ShowToast(string message, string? actionLabel = null, Func<Task>? action = null) =>
        _toast.Show(message, actionLabel, action);

    /// <summary>For the CLI (<c>flutterbar status</c>); called on IPC threads, so it reads immutable snapshots only.</summary>
    public StatusInfo Current()
    {
        var snapshot = _session.Snapshot;
        var labels = Volatile.Read(ref _labels);
        return new StatusInfo(
            snapshot.State.ToString(),
            labels.Project,
            labels.Branch,
            snapshot.Request?.Config.Name ?? labels.Config,
            snapshot.Request?.DeviceId ?? labels.Device,
            snapshot.ErrorCount);
    }

    /// <summary>The widget moved or resized (docking relayout, auto-hide slide): open popups follow it.</summary>
    public void OnWidgetMoved()
    {
        _flyout.Reposition();
        _toast.Reposition();
    }

    /// <summary>The widget window is being recreated: popups anchored to the old window must close.</summary>
    public void OnWidgetRecreating()
    {
        _flyout.Close();
        _toast.Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _lifetime.Cancel();
        _session.Changed -= OnSessionChanged;
        _vm.PropertyChanged -= OnViewModelChanged;
        _flyout.Close();
        _toast.Close();
        DisposeHeadWatcher();
        if (await Task.WhenAny(_ipcTask, Task.Delay(TimeSpan.FromSeconds(2))) != _ipcTask)
        {
            AppLog.Write("IPC server did not stop within 2 s");
        }
    }

    private Tools DiscoverTools()
    {
        var git = _paths.Find("git");
        var adb = AdbLocator.Find(Environment.GetEnvironmentVariable, File.Exists, _paths);
        AppLog.Write($"tools: git={git ?? "-"} adb={adb ?? "-"} flutter={_paths.Find("flutter") ?? "-"} fvm={_paths.Find("fvm") ?? "-"}");
        var devices = new DeviceService(_runner, _locator, adb);
        var run = new RunCoordinator(new RunOrchestrator(_session, devices, _locator));
        return new Tools(git is null ? null : new GitService(_runner, git), devices, run);
    }

    private void StartIpc()
    {
        try
        {
            var handler = new CommandHandler(_session, _log, this, _showWidget);
            var server = new IpcServer(PipeNames.ForCurrentUser(), handler.HandleAsync);
            _ipcTask = Task.Run(() => server.RunAsync(_lifetime.Token));
        }
        catch (InvalidOperationException ex)
        {
            _ipcTask = Task.FromException(ex);
        }
        _ = ObserveIpcAsync(_ipcTask);
    }

    // RunAsync can fault (e.g. the pipe cannot be created): report it, the widget keeps working without the CLI.
    private async Task ObserveIpcAsync(Task ipc)
    {
        try
        {
            await ipc;
            AppLog.Write("IPC server stopped");
        }
        catch (Exception ex)
        {
            AppLog.Write("IPC server failed", ex);
            if (!_lifetime.IsCancellationRequested)
            {
                ShowToast($"CLI flutterbar tidak tersedia: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The docking hides the widget with a native SW_HIDE (fullscreen / auto-hide), which WPF's IsVisible does not reflect:
    /// use the docking's state and the real window visibility.
    /// </summary>
    private bool WidgetShown()
    {
        if (_vm.IsHidden || _anchor() is not Window window)
        {
            return false;
        }
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(hwnd);
    }

    // Widget hidden: nothing may stay above a fullscreen app; a pending confirmation answers "Batal".
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetViewModel.IsHidden) && _vm.IsHidden && (_flyout.IsOpen || _toast.IsOpen))
        {
            AppLog.Write("widget hidden: flyout and toast closed");
            _flyout.Close();
            _toast.Close();
        }
    }

    private static bool SameProject(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One-session rule for a selection change (brief Step 7). While ▶ is still being prepared nothing runs yet: the run
    /// is cancelled ("Run dibatalkan"). Once flutter is being launched, wait for the launch and ask like for any session:
    /// Ya = stop, Batal = keep the session and the old selection. True = the caller may change the selection.
    /// </summary>
    private async Task<bool> MayChangeSelectionAsync(string what)
    {
        var run = (await _tools).Run;
        switch (run.CancelPreparation())
        {
            case PreparationCancel.Cancelled:
                AppLog.Write($"run preparation cancelled: {what} changed before flutter was launched");
                ShowToast("Run dibatalkan");
                return true;
            case PreparationCancel.Launching:
                AppLog.Write($"{what} changed while flutter is being launched: waiting for the launch, then asking");
                await run.WaitForLaunchAsync();
                break;
        }
        return await ConfirmStopAsync(what);
    }

    // Any thread, possibly stale: marshal and re-read. A session that just failed gets a toast with its message.
    private void OnSessionChanged(SessionSnapshot ignored) => _dispatcher.BeginInvoke(() =>
    {
        var snapshot = _session.Snapshot;
        if (snapshot.State == SessionState.Failed && _lastState != SessionState.Failed && !_disposed)
        {
            ShowToast($"Sesi gagal: {snapshot.FailureMessage ?? "flutter berhenti"}");
        }
        _lastState = snapshot.State;
    });

    /// <summary>Observes a fire-and-forget operation started from a click or event: failures are logged and toasted.</summary>
    private void Fire(Task task, string what) => _ = ObserveAsync(task, what);

    private async Task ObserveAsync(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            AppLog.Write($"{what}: cancelled (exit)");
        }
        catch (Exception ex) // top of a fire-and-forget chain: report, never leave it unobserved
        {
            AppLog.Write($"{what} failed", ex);
            if (!_disposed)
            {
                ShowToast($"{what} gagal: {ex.Message}");
            }
        }
    }

    /// <summary>Refreshes what the CLI status reports (immutable record, read from IPC threads).</summary>
    private void PublishLabels()
    {
        var root = Settings.CurrentProject;
        ProjectState? state = null;
        if (root is not null)
        {
            Settings.TryGetProject(root, out state);
        }
        Volatile.Write(ref _labels, new StatusLabels(
            root is null ? null : _vm.ProjectName,
            _vm.BranchVisible ? _vm.Branch : null,
            _configChip?.Selected?.Name ?? state?.Config,
            state?.DeviceId));
    }

    private sealed record Tools(GitService? Git, DeviceService Devices, RunCoordinator Run);

    /// <summary>What <c>flutterbar status</c> reports besides the session; null = nothing selected.</summary>
    private sealed record StatusLabels(string? Project, string? Branch, string? Config, string? Device);
}
