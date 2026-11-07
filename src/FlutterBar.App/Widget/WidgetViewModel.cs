using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using FlutterBar.App.Diagnostics;
using FlutterBar.Core.Layout;
using FlutterBar.Core.Sessions;

namespace FlutterBar.App.Widget;

/// <summary>Which chip/area was clicked; Task 14 opens the matching flyout.</summary>
internal enum WidgetChip
{
    Project,
    Branch,
    Config,
    Device,

    /// <summary>Compact name area or ButtonsOnly status dot: the combined flyout.</summary>
    Combined,
}

/// <summary>
/// State of the taskbar widget (spec §7.1). Session state comes from <see cref="RunSession.Changed"/>, which may fire on
/// any thread with a stale argument: it is marshalled to the dispatcher and <see cref="RunSession.Snapshot"/> re-read.
/// Reload/Restart/Stop call the session; Run, the log and the chips only raise events (Task 14/15 orchestrate them).
/// </summary>
internal sealed class WidgetViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan ReloadTimeout = TimeSpan.FromSeconds(60);

    private readonly RunSession _session;
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly RelayCommand _run;
    private readonly RelayCommand _reload;
    private readonly RelayCommand _restart;
    private readonly RelayCommand _stop;
    private bool _snapshotPending;
    private SessionSnapshot _snapshot;
    private SessionControls _controls;
    private WidgetSize _effectiveSize = WidgetSize.Full;
    private bool _taskbarFull;
    private bool _isHidden;
    private string _projectName = "Pilih project";
    private string _branch = "—";
    private string _configName = "Pilih config";
    private string _deviceName = "Pilih device";
    private string? _notice;

    public WidgetViewModel(RunSession session, Dispatcher dispatcher)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher;
        _snapshot = session.Snapshot;
        _controls = SessionControls.From(_snapshot);
        _run = new RelayCommand(() => RunRequested?.Invoke(), () => _controls.ShowRun);
        _reload = new RelayCommand(() => _ = ReloadAsync(fullRestart: false), () => _controls.ReloadEnabled);
        _restart = new RelayCommand(() => _ = ReloadAsync(fullRestart: true), () => _controls.RestartEnabled);
        _stop = new RelayCommand(() => _ = StopAsync(), () => _controls.StopEnabled);
        OpenLogCommand = new RelayCommand(() => OpenLogRequested?.Invoke());
        ProjectChipCommand = new RelayCommand(() => ChipRequested?.Invoke(WidgetChip.Project));
        BranchChipCommand = new RelayCommand(() => ChipRequested?.Invoke(WidgetChip.Branch));
        ConfigChipCommand = new RelayCommand(() => ChipRequested?.Invoke(WidgetChip.Config));
        DeviceChipCommand = new RelayCommand(() => ChipRequested?.Invoke(WidgetChip.Device));
        CombinedChipCommand = new RelayCommand(() => ChipRequested?.Invoke(WidgetChip.Combined));
        // spec §7.1 ButtonsOnly: the dot opens the combined flyout; a red dot opens the log (at the error, Task 15)
        StatusDotCommand = new RelayCommand(() =>
        {
            if (_controls.Dot == StatusDot.Error)
            {
                OpenLogRequested?.Invoke();
            }
            else
            {
                ChipRequested?.Invoke(WidgetChip.Combined);
            }
        });
        _session.Changed += OnSessionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>▶ pressed: Task 14 (AppController.RunAsync) validates project/config/device and starts the session.</summary>
    public event Action? RunRequested;

    /// <summary>🖥 pressed: Task 15 opens the LogWindow.</summary>
    public event Action? OpenLogRequested;

    /// <summary>A chip was clicked: Task 14 opens the flyout.</summary>
    public event Action<WidgetChip>? ChipRequested;

    public SessionSnapshot Session => _snapshot;

    /// <summary>Derived button/dot state (pure mapping in Core, <see cref="SessionControls.From"/>).</summary>
    public SessionControls Controls => _controls;

    public WidgetSize EffectiveSize
    {
        get => _effectiveSize;
        set => Set(ref _effectiveSize, value);
    }

    /// <summary>Nothing fits, not even ButtonsOnly: the widget shows "Taskbar penuh — gunakan Drag widget".</summary>
    public bool TaskbarFull
    {
        get => _taskbarFull;
        set => Set(ref _taskbarFull, value);
    }

    public bool IsHidden
    {
        get => _isHidden;
        set => Set(ref _isHidden, value);
    }

    public string ProjectName
    {
        get => _projectName;
        set => Set(ref _projectName, value);
    }

    public string Branch
    {
        get => _branch;
        set => Set(ref _branch, value);
    }

    public string ConfigName
    {
        get => _configName;
        set => Set(ref _configName, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => Set(ref _deviceName, value);
    }

    /// <summary>Last reload/restart/stop problem, shown in the status tooltip until the next action (toasts come in Task 14).</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value))
            {
                OnPropertyChanged(nameof(StatusToolTip));
            }
        }
    }

    public string StatusToolTip => _notice is null ? _controls.StatusText : $"{_controls.StatusText}\n{_notice}";

    public ICommand RunCommand => _run;

    public ICommand ReloadCommand => _reload;

    public ICommand RestartCommand => _restart;

    public ICommand StopCommand => _stop;

    public ICommand OpenLogCommand { get; }

    public ICommand ProjectChipCommand { get; }

    public ICommand BranchChipCommand { get; }

    public ICommand ConfigChipCommand { get; }

    public ICommand DeviceChipCommand { get; }

    public ICommand CombinedChipCommand { get; }

    public ICommand StatusDotCommand { get; }

    public void Dispose() => _session.Changed -= OnSessionChanged;

    // Any thread; the argument may be stale. Coalesce and re-read the snapshot on the UI thread.
    private void OnSessionChanged(SessionSnapshot ignored)
    {
        lock (_gate)
        {
            if (_snapshotPending)
            {
                return;
            }
            _snapshotPending = true;
        }
        _dispatcher.BeginInvoke(() =>
        {
            lock (_gate)
            {
                _snapshotPending = false;
            }
            ApplySnapshot(_session.Snapshot);
        });
    }

    private void ApplySnapshot(SessionSnapshot snapshot)
    {
        if (snapshot == _snapshot)
        {
            return;
        }
        _snapshot = snapshot;
        _controls = SessionControls.From(snapshot);
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(Controls));
        OnPropertyChanged(nameof(StatusToolTip));
        _run.Refresh();
        _reload.Refresh();
        _restart.Refresh();
        _stop.Refresh();
    }

    private async Task ReloadAsync(bool fullRestart)
    {
        var label = fullRestart ? "Hot restart" : "Hot reload";
        Notice = null;
        try
        {
            var outcome = await _session.ReloadAsync(fullRestart, ReloadTimeout);
            if (!outcome.Success)
            {
                Notice = $"{label} gagal: {outcome.Message}";
                AppLog.Write($"{label} from widget failed ({outcome.Failure}): {outcome.Message}");
            }
        }
        catch (Exception ex) // fire-and-forget command: report here, never let it go unobserved
        {
            Notice = $"{label} gagal: {ex.Message}";
            AppLog.Write($"{label} from widget threw", ex);
        }
    }

    private async Task StopAsync()
    {
        Notice = null;
        try
        {
            await _session.StopAsync();
        }
        catch (Exception ex) // fire-and-forget command: report here, never let it go unobserved
        {
            Notice = $"Stop gagal: {ex.Message}";
            AppLog.Write("stop from widget threw", ex);
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
