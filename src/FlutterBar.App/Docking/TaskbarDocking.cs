using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Native;
using FlutterBar.Core.Layout;

namespace FlutterBar.App.Docking;

/// <summary>
/// Docks a borderless WPF window onto the primary taskbar (spec §7.3), porting the technique proven by the docking spike
/// v3.2 (findings doc "Notes for Task 13"). Lifecycle, hooks, timer and the reassert loop live here; owner in
/// <c>TaskbarDocking.Owner.cs</c>, placement/auto-hide/UIA query in
/// <c>TaskbarDocking.Placement.cs</c>, drag/resize in <c>TaskbarDocking.Drag.cs</c>, popup suspension in
/// <c>TaskbarDocking.Popups.cs</c>. UI thread only, except the background UIA query. One instance per window: when the
/// window must be recreated the instance tears itself down completely and raises <see cref="RecreateRequested"/>.
/// </summary>
internal sealed partial class TaskbarDocking
{
    private const double OffScreen = -32000;
    private static readonly TimeSpan RecreateMinInterval = TimeSpan.FromSeconds(20);
    private static DateTime s_lastRecreate = DateTime.MinValue; // UI thread only

    private readonly Window _window;
    private readonly string _id = Guid.NewGuid().ToString("N")[..6];
    private readonly DispatcherTimer _timer;
    private readonly TaskbarProbe _probe;
    private readonly FullscreenDetector _fullscreen = new();
    private readonly NativeMethods.WinEventDelegate _foregroundCallback; // kept in fields: the hooks must not lose them to the GC
    private readonly NativeMethods.WinEventDelegate _locationCallback;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private IntPtr _tray;
    private IntPtr _trayNotify;
    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private uint _taskbarCreatedMessage;
    private bool _started;
    private volatile bool _dead;
    private bool _inReassert;
    private bool _locationPending;
    private int _ownerFails;
    private bool _autoHide;
    private volatile bool _autoHidden;

    public TaskbarDocking(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.Left = OffScreen; // no flash at the default position before the first placement
        _window.Top = OffScreen;
        _timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _probe = new TaskbarProbe(() => _dead);
        _foregroundCallback = OnForeground;
        _locationCallback = OnLocation;
    }

    /// <summary>Free strip (physical px) the widget docks into in auto mode; null in manual/drag mode or when nothing fits.</summary>
    public event Action<Box?>? AreaChanged;

    /// <summary>Raised when the drag gesture is locked by unchecking Drag widget: offset from the taskbar's right edge (physical px).</summary>
    public event Action<int>? ManualOffsetChanged;

    /// <summary>Size to render, and whether the taskbar is full (nothing fits even ButtonsOnly: show the hint).</summary>
    public event Action<WidgetSize, bool>? EffectiveSizeChanged;

    /// <summary>Hidden for fullscreen or auto-hide.</summary>
    public event Action<bool>? HiddenChanged;

    /// <summary>The instance is dead (window gone or owner attach failing); the app must create a new window and docking.</summary>
    public event Action<string>? RecreateRequested;

    /// <summary>Preferred size (settings <c>widget.size</c>); auto mode steps down from it.</summary>
    public WidgetSize PreferredSize
    {
        get => _preferred;
        set
        {
            if (_preferred == value)
            {
                return;
            }
            _preferred = value;
            InvalidateLayout();
        }
    }

    /// <summary>Required width per size in DIPs (measured by the widget). Defaults to the spike widths.</summary>
    public Func<IReadOnlyDictionary<WidgetSize, double>>? RequiredWidths { get; set; }

    public bool IsManual => _manualOffset is not null;

    public string InstanceId => _id;

    public void Start()
    {
        if (_started || _dead)
        {
            return;
        }
        _started = true;
        s_active = this;
        _window.Closed += OnWindowClosed;
        _window.ContextMenuOpening += OnContextMenuOpening;
        AttachDragHandlers();
        if (new WindowInteropHelper(_window).Handle != IntPtr.Zero)
        {
            OnSourceInitialized(_window, EventArgs.Empty);
        }
        else
        {
            _window.SourceInitialized += OnSourceInitialized;
        }
    }

    /// <summary>Intentional stop (app exit): full teardown, no recreate.</summary>
    public void Stop()
    {
        if (_dead)
        {
            return;
        }
        Teardown();
        AppLog.Write($"docking stopped inst={_id}");
    }

    /// <summary>Manual mode at <paramref name="offsetFromRight"/> (physical px from the taskbar's right edge, clamped).</summary>
    public void SetManual(int offsetFromRight)
    {
        _manualOffset = Math.Max(0, offsetFromRight);
        AppLog.Write($"manual position inst={_id}: offsetFromRight={_manualOffset}");
        RequestReassert("manual");
    }

    /// <summary>Back to auto mode: leaves drag mode cleanly, forgets the manual offset, next layout applies immediately.</summary>
    public void ResetToAuto()
    {
        if (_dragMode || _gesture != Gesture.None)
        {
            EndGesture();
            _dragMode = false;
            _dragX = null;
            DragModeChanged?.Invoke(false);
        }
        _manualOffset = null;
        _debouncer.Reset();
        _evaluatedLayout = null;
        AppLog.Write($"reset to auto position inst={_id} (drag mode exited, immediate-apply armed)");
        RequestReassert("reset-auto");
    }

    /// <summary>Content or preferred size changed: re-evaluate the placement now (applied immediately).</summary>
    public void InvalidateLayout() => RequestReassert("content");

    private void RequestReassert(string reason)
    {
        if (_started && !_dead && _hwnd != IntPtr.Zero)
        {
            _window.Dispatcher.BeginInvoke(() => Reassert(reason), DispatcherPriority.Background);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _window.SourceInitialized -= OnSourceInitialized;
        if (_dead)
        {
            return;
        }
        _hwnd = new WindowInteropHelper(_window).Handle;
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundCallback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (_foregroundHook == IntPtr.Zero)
        {
            AppLog.Write($"SetWinEventHook(FOREGROUND) failed inst={_id}");
        }
        _autoHide = NativeMethods.IsAutoHide();
        AppLog.Write($"widget window created inst={_id} hwnd=0x{_hwnd:X} autoHide={_autoHide}");
        var tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
        {
            EnsureOwner(tray, "startup", timerTick: false); // owner set once, right after the HWND exists
        }
        // SourceInitialized runs inside Show() BEFORE WPF's own ShowWindow: the first reassert (which may hide the
        // window for auto-hide/fullscreen) must run after the window is really shown.
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_dead)
            {
                return;
            }
            _timer.Tick += OnTimer;
            _timer.Start();
            Reassert("startup");
        }, DispatcherPriority.Loaded);
    }

    private void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!_dead)
        {
            Reassert("foreground");
        }
    }

    private void OnLocation(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Only the taskbar window itself (auto-hide slide, resize) and its tray child; the hook is per explorer process, so
        // moving a File Explorer window would otherwise trigger reassert bursts. The 600 ms timer covers everything else.
        if (_dead || idObject != NativeMethods.OBJID_WINDOW || _locationPending || (hwnd != _tray && hwnd != _trayNotify))
        {
            return;
        }
        _locationPending = true; // coalesce bursts into one reassert
        _window.Dispatcher.BeginInvoke(() =>
        {
            _locationPending = false;
            if (!_dead)
            {
                Reassert("locationchange");
            }
        }, DispatcherPriority.Background);
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        if (_dead)
        {
            return;
        }
        if (!NativeMethods.IsWindow(_hwnd))
        {
            _ = RequestRecreate("IsWindow=false"); // rate-limited: retried on the next tick
            return;
        }
        if (_ownerFails >= 3 && RequestRecreate($"owner attach failed {_ownerFails}x on timer ticks"))
        {
            return;
        }
        PopupFailsafe();
        _autoHide = NativeMethods.IsAutoHide();
        Reassert("timer");
    }

    private void OnWindowClosed(object? sender, EventArgs e) => _ = RequestRecreate("window closed unexpectedly"); // rate-limited: the timer retries via IsWindow

    /// <summary>
    /// Every recreate path (owner failures, IsWindow=false, Closed) is rate-limited to once per 20 s process-wide. While
    /// limited the instance stays alive and the timer retries on its next tick. Returns true when the recreate happened.
    /// </summary>
    private bool RequestRecreate(string reason)
    {
        if (_dead)
        {
            return false;
        }
        if (DateTime.UtcNow - s_lastRecreate < RecreateMinInterval)
        {
            AppLog.Limited("recreate-limit", $"inst={_id} recreate wanted ({reason}) but rate-limited (max once per 20 s); timer retries", 5);
            return false;
        }
        s_lastRecreate = DateTime.UtcNow;
        Teardown();
        AppLog.Write($"widget window destroyed inst={_id} ({reason}) -> old instance stopped, recreating");
        RecreateRequested?.Invoke(reason);
        return true;
    }

    /// <summary>Full stop: after this no handler of this instance does anything or logs (all check <see cref="_dead"/>).</summary>
    private void Teardown()
    {
        if (_dragMode || _gesture != Gesture.None)
        {
            AppLog.Write($"inst={_id} teardown while drag mode active: ending drag, unsaved drag state dropped (x={_dragX})");
            EndGesture();
            _dragMode = false;
            _dragX = null;
        }
        _dead = true;
        _timer.Stop();
        _timer.Tick -= OnTimer;
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
        }
        if (_locationHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_locationHook);
        }
        _foregroundHook = _locationHook = IntPtr.Zero;
        _source?.RemoveHook(WndProc);
        _source = null;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnWindowClosed;
        _window.ContextMenuOpening -= OnContextMenuOpening;
        DetachDragHandlers();
        _openPopups.Clear();
        if (ReferenceEquals(s_active, this))
        {
            s_active = null;
        }
    }

    /// <summary>
    /// The core loop, run on every foreground change, taskbar location change, 600 ms timer tick and state change:
    /// owner check, auto-hide and fullscreen visibility, placement, and the topmost reassert. Guarded against re-entrancy.
    /// </summary>
    private void Reassert(string reason)
    {
        if (_inReassert || _dead || _hwnd == IntPtr.Zero)
        {
            return;
        }
        _inReassert = true;
        try
        {
            if (reason is not ("timer" or "foreground" or "locationchange" or "layout"))
            {
                AppLog.Limited("reason:" + reason, $"reassert inst={_id}: {reason}");
            }
            var tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero)
            {
                AppLog.Limited("reassert-notray", $"reassert inst={_id}: Shell_TrayWnd not found", 5);
                return;
            }
            var timerTick = reason == "timer";
            EnsureOwner(tray, reason, timerTick);
            if (NativeMethods.WindowBox(tray) is not { } taskbar)
            {
                return;
            }
            UpdateAutoHide(taskbar, reason);
            if (!_autoHidden && !_dragMode && (timerTick || reason == "locationchange"))
            {
                KickQuery();
            }
            if (_fullscreen.Update(_hwnd, tray, _autoHide))
            {
                AppLog.Write($"fullscreen -> {(_fullscreen.IsFullscreen ? "YES (hide widget)" : "NO (show widget)")} inst={_id} [{reason}]");
            }
            Place(tray, taskbar, wantVisible: !_fullscreen.IsFullscreen && !_autoHidden);
        }
        catch (Exception ex)
        {
            // Reassert also runs from WinEvent callbacks: an exception escaping into native code would kill the process.
            AppLog.Limited("reassert-fail", $"Reassert({reason}) failed inst={_id}: {ex}", 5);
        }
        finally
        {
            _inReassert = false;
        }
    }
}
