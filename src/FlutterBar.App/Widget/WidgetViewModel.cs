using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using FlutterBar.Core.Layout;
using FlutterBar.Core.Orchestration;
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
/// The buttons and chips only raise events: <c>AppController</c> (Task 14) runs, reloads, stops and opens the flyouts,
/// the LogWindow (Task 15) handles the log button.
/// </summary>
internal sealed class WidgetViewModel : INotifyPropertyChanged, IDisposable
{
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
    private string _projectName = UiText.PickProject;
    private string _branch = "—";
    private bool _branchVisible;
    private string? _branchToolTip;
    private string _configName = UiText.PickConfig;
    private bool _configHasError;
    private string? _configToolTip;
    private string _deviceName = UiText.PickDevice;
    private string? _notice;
    private bool _preparing;

    public WidgetViewModel(RunSession session, Dispatcher dispatcher)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher;
        _snapshot = session.Snapshot;
        _controls = SessionControls.From(_snapshot);
        _run = new RelayCommand(() => RunRequested?.Invoke(), () => _controls.ShowRun && !_preparing);
        _reload = new RelayCommand(() => ReloadRequested?.Invoke(false), () => _controls.ReloadEnabled);
        _restart = new RelayCommand(() => ReloadRequested?.Invoke(true), () => _controls.RestartEnabled);
        _stop = new RelayCommand(() => StopRequested?.Invoke(), () => _controls.StopEnabled);
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

    /// <summary>⚡ (false) or 🔄 (true) pressed.</summary>
    public event Action<bool>? ReloadRequested;

    /// <summary>⏹ pressed.</summary>
    public event Action? StopRequested;

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

    /// <summary>Nothing fits, not even ButtonsOnly: the widget shows "Taskbar is full — use Drag widget".</summary>
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

    /// <summary>False when the project is not a git repo (or git is missing): the branch chip is collapsed (spec §5.5).</summary>
    public bool BranchVisible
    {
        get => _branchVisible;
        set => Set(ref _branchVisible, value);
    }

    public string? BranchToolTip
    {
        get => _branchToolTip;
        set => Set(ref _branchToolTip, value);
    }

    public string ConfigName
    {
        get => _configName;
        set => Set(ref _configName, value);
    }

    /// <summary>launch.json invalid or the selected config is gone: the chip turns red (spec §10).</summary>
    public bool ConfigHasError
    {
        get => _configHasError;
        set => Set(ref _configHasError, value);
    }

    /// <summary>program / flavor / mode of the selected config, or the validation errors.</summary>
    public string? ConfigToolTip
    {
        get => _configToolTip;
        set => Set(ref _configToolTip, value);
    }

    /// <summary>▶ was pressed and the run is being prepared (device refresh etc.): ▶ is disabled meanwhile.</summary>
    public bool Preparing
    {
        get => _preparing;
        set
        {
            if (Set(ref _preparing, value))
            {
                _run.Refresh();
                OnPropertyChanged(nameof(StatusToolTip));
            }
        }
    }

    public string DeviceName
    {
        get => _deviceName;
        set => Set(ref _deviceName, value);
    }

    /// <summary>Last problem (also shown as a toast), kept in the status tooltip until the next action.</summary>
    public string? Notice
    {
        get => _notice;
        set
        {
            if (Set(ref _notice, value))
            {
                OnPropertyChanged(nameof(StatusToolTip));
            }
        }
    }

    public string StatusToolTip
    {
        get
        {
            var text = _preparing ? "Preparing run…" : _controls.StatusText;
            return _notice is null ? text : $"{text}\n{_notice}";
        }
    }

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
