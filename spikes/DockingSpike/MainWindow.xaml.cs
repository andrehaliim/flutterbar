using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DockingSpike;

public partial class MainWindow : Window
{
    const int Gap = 8;
    static readonly HashSet<string> ShellClasses = new() { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW" };
    static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "ShellHost" };

    IntPtr _hwnd, _tray, _fgHook, _locHook;
    uint _taskbarCreatedMsg;
    Native.WinEventDelegate? _fgCb, _locCb; // keep alive (GC)
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly Dictionary<string, DateTime> _lastLog = new();
    readonly Dictionary<uint, string> _procNames = new();
    bool _locPending, _queryRunning, _hidden, _fullscreen, _inReassert, _dead, _noGap;
    bool _autoHide;
    string _align = "tengah";
    DateTime _lastQuery = DateTime.MinValue;
    volatile Taskbar.Result? _layout;
    string _lastReason = "-";
    string _lastPosLog = "";
    int _qunsLogged = -1;
    (int x, int y, int w, int h) _lastPos;
    // manual/drag state
    SpikeSettings _settings = new();
    bool _menuOpen, _dragMode, _dragged, _dragging;
    int? _dragX;
    int _dragStartCursor, _dragStartX;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        _timer.Tick += (_, _) => OnTimer();
        Closed += OnClosed;
    }

    // Debug: block the UI thread to test whether taskbar input stalls (cross-process owner shares input queue).
    void OnFreeze(object sender, RoutedEventArgs e)
    {
        Taskbar.Log($"{Now()} FREEZE 5s start (UI thread blocked; test whether taskbar stays clickable)");
        Thread.Sleep(5000);
        Taskbar.Log($"{Now()} FREEZE end");
    }

    // ---- context menu: while open, the topmost reassert is suspended (reusable for tooltips/popups) ----
    void OnMenuOpening(object sender, ContextMenuEventArgs e) { _menuOpen = true; }

    void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        _menuOpen = true;
        if (sender is ContextMenu cm && PresentationSource.FromVisual(cm) is HwndSource hs)
        {
            Native.SetWindowPos(hs.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE);
            Taskbar.Log($"{Now()} menu opened: popup hwnd=0x{hs.Handle:X} set TOPMOST, widget reassert suspended");
        }
        else Taskbar.Log($"{Now()} menu opened: popup HwndSource not found");
    }

    void OnMenuClosed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        Taskbar.Log($"{Now()} menu closed: reassert resumed");
        Reassert("menu-closed");
    }

    // ---- manual position / drag ----
    void OnDragToggle(object sender, RoutedEventArgs e) => SetDragMode(DragItem.IsChecked);

    void SetDragMode(bool on)
    {
        if (on == _dragMode) return;
        _dragMode = on;
        DragItem.IsChecked = on;
        Frame.BorderBrush = on ? SystemParameters.WindowGlassBrush : Brushes.Transparent;
        Cursor = on ? Cursors.SizeWE : null;
        if (on)
        {
            _dragged = false;
            Native.GetWindowRect(_hwnd, out var r);
            _dragX = r.Left;
            Taskbar.Log($"{Now()} drag mode ON (x={r.Left})");
        }
        else
        {
            if (_dragged && _dragX is int dx && Native.GetWindowRect(_tray, out var tb))
            {
                int w = (int)(300 * Native.GetDpiForWindow(_hwnd) / 96.0);
                _settings = new SpikeSettings { Mode = "manual", OffsetFromRight = Math.Max(0, tb.Right - (dx + w)) };
                _settings.Save();
                Taskbar.Log($"{Now()} drag mode OFF -> position locked, mode=manual offsetFromRight={_settings.OffsetFromRight}");
            }
            else Taskbar.Log($"{Now()} drag mode OFF (no drag, mode stays {_settings.Mode})");
            _dragX = null;
        }
        Reassert("drag-toggle");
    }

    void OnResetAuto(object sender, RoutedEventArgs e)
    {
        _dragMode = false; _dragX = null; _dragging = false;
        DragItem.IsChecked = false;
        Frame.BorderBrush = Brushes.Transparent;
        Cursor = null;
        _settings = new SpikeSettings();
        SpikeSettings.Delete();
        _lastPos = default;
        Taskbar.Log($"{Now()} reset to auto position");
        Reassert("reset-auto");
    }

    void OnDragDown(object sender, MouseButtonEventArgs e)
    {
        if (!_dragMode) return;
        Native.GetCursorPos(out var p);
        Native.GetWindowRect(_hwnd, out var r);
        _dragStartCursor = p.X; _dragStartX = r.Left;
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || !IsMouseCaptured) return;
        if (!Native.GetCursorPos(out var p) || !Native.GetWindowRect(_tray, out var tb)) return;
        uint dpi = Native.GetDpiForWindow(_hwnd);
        int w = (int)(300 * dpi / 96.0), h = (int)(40 * dpi / 96.0);
        int nx = Math.Clamp(_dragStartX + p.X - _dragStartCursor, tb.Left, Math.Max(tb.Left, tb.Right - w));
        int y = tb.Top + (tb.Bottom - tb.Top - h) / 2;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, nx, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _dragX = nx; _dragged = true; _lastPos = (nx, y, w, h);
    }

    void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Taskbar.Log($"{Now()} drag end x={_dragX}");
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _settings = SpikeSettings.Load();
        var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _fgCb = OnForeground;
        _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgCb, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        if (_fgHook == IntPtr.Zero) Taskbar.Log("SetWinEventHook(FOREGROUND) failed");
        _locCb = OnLocation;
        _autoHide = IsAutoHide();
        _align = ReadAlignment();
        AttachToTaskbar("startup", true);
        _timer.Start();
        Reassert("startup");
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            Taskbar.Log($"{Now()} TaskbarCreated received (explorer restarted)");
            _layout = null;
            AttachToTaskbar("TaskbarCreated", true);
            Reassert("TaskbarCreated");
        }
        return IntPtr.Zero;
    }

    // Set the taskbar as owner; (re)install the location hook only when the tray handle changed (or forced).
    void AttachToTaskbar(string why, bool forceHook)
    {
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) { LogLimited("attach-notfound", $"{Now()} AttachToTaskbar({why}): Shell_TrayWnd not found"); return; }
        bool changed = tray != _tray;
        if (changed || forceHook || _locHook == IntPtr.Zero)
        {
            if (_locHook != IntPtr.Zero) { Native.UnhookWinEvent(_locHook); _locHook = IntPtr.Zero; }
            uint tid = Native.GetWindowThreadProcessId(tray, out var pid);
            _locHook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _locCb!, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
            Taskbar.Log($"{Now()} location hook ({why}): tray=0x{tray:X} pid={pid} tid={tid} locHook={(_locHook == IntPtr.Zero ? "FAILED" : "ok")}");
        }
        _tray = tray;
        Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT, tray);
        var owner = Native.GetWindow(_hwnd, Native.GW_OWNER);
        LogLimited("attach", $"{Now()} AttachToTaskbar({why}): tray=0x{tray:X} owner=0x{owner:X}");
    }

    void LogLimited(string key, string msg)
    {
        if (_lastLog.TryGetValue(key, out var t) && (DateTime.Now - t).TotalSeconds < 2) return;
        _lastLog[key] = DateTime.Now;
        Taskbar.Log(msg);
    }

    void Cleanup()
    {
        _timer.Stop();
        if (_fgHook != IntPtr.Zero) Native.UnhookWinEvent(_fgHook);
        if (_locHook != IntPtr.Zero) Native.UnhookWinEvent(_locHook);
        _fgHook = _locHook = IntPtr.Zero;
    }

    void Recreate(string why)
    {
        if (_dead) return;
        _dead = true;
        Cleanup();
        if (Program.Exiting) return;
        Taskbar.Log($"{Now()} widget window destroyed ({why}) -> recreating");
        Dispatcher.BeginInvoke(() => new MainWindow().Show());
    }

    void OnClosed(object? sender, EventArgs e) => Recreate("Closed event, owner gone?");

    void OnTimer()
    {
        if (_hwnd != IntPtr.Zero && !Native.IsWindow(_hwnd)) { Recreate("IsWindow=false"); return; }
        _autoHide = IsAutoHide();
        _align = ReadAlignment();
        Reassert("timer");
    }

    void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        => Reassert("foreground:" + Native.ClassOf(hwnd));

    void OnLocation(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || _locPending) return;
        _locPending = true;
        Dispatcher.BeginInvoke(() => { _locPending = false; Reassert("locationchange"); }, DispatcherPriority.Background);
    }

    static string Now() => DateTime.Now.ToString("HH:mm:ss.fff");

    void LogReason(string reason)
    {
        if (reason == "timer") return;
        var key = reason.StartsWith("foreground:", StringComparison.Ordinal) ? "foreground" : reason;
        LogLimited("reason:" + key, $"{Now()} reassert: {reason}");
    }

    void KickQuery(IntPtr tray)
    {
        if (_queryRunning || (DateTime.Now - _lastQuery).TotalSeconds < 1) return;
        _queryRunning = true;
        _lastQuery = DateTime.Now;
        Task.Run(() =>
        {
            try
            {
                var r = Taskbar.Compute(tray);
                if (r is not null) _layout = r;
            }
            catch (Exception ex)
            {
                Taskbar.Log($"{Now()} UIA query failed (keeping last result): {ex.Message}");
            }
            finally { _queryRunning = false; }
        });
    }

    static string ReadAlignment()
    {
        try
        {
            var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl", null);
            return v is int i && i == 0 ? "kiri" : "tengah";
        }
        catch (Exception ex)
        {
            Taskbar.Log($"{Now()} read TaskbarAl failed: {ex.Message}");
            return "tengah?";
        }
    }

    static bool IsAutoHide()
    {
        var abd = new Native.APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>() };
        return ((uint)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd) & Native.ABS_AUTOHIDE) != 0;
    }

    // Every foreground window is classified by its owning process; the class list is only a fast path.
    bool IsShellForeground(IntPtr fg, string cls)
    {
        if (ShellClasses.Contains(cls)) return true;
        try
        {
            Native.GetWindowThreadProcessId(fg, out var pid);
            if (!_procNames.TryGetValue(pid, out var name))
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
                if (_procNames.Count > 128) _procNames.Clear();
                _procNames[pid] = name;
            }
            return ShellProcesses.Contains(name);
        }
        catch (Exception ex)
        {
            LogLimited("proc-fail", $"{Now()} process lookup failed for class {cls}: {ex.Message} (treated as shell)");
            return true;
        }
    }

    bool DetectFullscreen(IntPtr tray)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return _fullscreen;
        var cls = Native.ClassOf(fg);
        if (IsShellForeground(fg, cls)) return false;
        var mon = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
        if (mon != Native.MonitorFromWindow(tray, Native.MONITOR_DEFAULTTONEAREST)) return false;
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi) || !Native.GetWindowRect(fg, out var r)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    void Reassert(string reason)
    {
        if (_inReassert || _dead || _hwnd == IntPtr.Zero) return;
        _inReassert = true;
        try
        {
            _lastReason = reason;
            LogReason(reason);
            var tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) { Info.Text = "taskbar not found"; return; }
            if (tray != _tray || Native.GetWindow(_hwnd, Native.GW_OWNER) != tray) AttachToTaskbar("handle/owner changed", false);
            bool timer = reason == "timer";
            if (timer) KickQuery(tray);

            bool fs = !_autoHide && DetectFullscreen(tray);
            if (timer)
            {
                Native.SHQueryUserNotificationState(out var quns);
                if (quns != _qunsLogged) { _qunsLogged = quns; Taskbar.Log($"{Now()} SHQueryUserNotificationState={quns} fullscreenRule={fs} autoHide={_autoHide}"); }
            }
            if (fs != _fullscreen) { _fullscreen = fs; Taskbar.Log($"{Now()} fullscreen -> {(fs ? "YES (hide widget)" : "NO (show widget)")} [{reason}]"); }

            uint dpi = Native.GetDpiForWindow(_hwnd);
            double scale = dpi / 96.0;
            int w = (int)(300 * scale), h = (int)(40 * scale);
            var res = _layout;
            int x = 0, y = 0;
            string gapTxt = "?";
            bool haveGap = false;
            Native.GetWindowRect(tray, out var tbr);
            int yDock = tbr.Top + (tbr.Bottom - tbr.Top - h) / 2;
            int xMax = Math.Max(tbr.Left, tbr.Right - w);
            bool manual = !_dragMode && _settings.Mode == "manual" && _settings.OffsetFromRight is not null;
            if (_dragMode)
            {
                x = Math.Clamp(_dragX ?? tbr.Left, tbr.Left, xMax); y = yDock; haveGap = true;
                gapTxt = "(drag)";
            }
            else if (manual)
            {
                x = Math.Clamp(tbr.Right - _settings.OffsetFromRight!.Value - w, tbr.Left, xMax); y = yDock; haveGap = true;
                gapTxt = "(manual, no auto-avoid)";
            }
            else if (res is not null)
            {
                var gaps = Taskbar.Gaps(res, Gap);
                gapTxt = string.Join(" ", gaps.Select(g => $"[{g.L}..{g.R}]={g.R - g.L}"));
                for (int i = gaps.Count - 1; i >= 0; i--)
                {
                    if (gaps[i].R - gaps[i].L >= w)
                    {
                        x = gaps[i].R - w;
                        y = res.Taskbar.Top + (res.Taskbar.Bottom - res.Taskbar.Top - h) / 2;
                        haveGap = true;
                        gapTxt += $" -> pakai [{gaps[i].L}..{gaps[i].R}]";
                        break;
                    }
                }
                if (!haveGap) gapTxt += " -> TIDAK ADA gap cukup";
            }
            bool noGap = !_dragMode && !manual && res is not null && !haveGap;
            if (noGap != _noGap) { _noGap = noGap; Taskbar.Log($"{Now()} gap fit -> {(noGap ? "NONE (hide widget)" : "ok")} gaps: {gapTxt}"); }
            bool wantVisible = !fs && !noGap;

            if (!wantVisible)
            {
                if (!_hidden) { Native.ShowWindow(_hwnd, 0); _hidden = true; }
            }
            else
            {
                if (_hidden) { Native.ShowWindow(_hwnd, 4); _hidden = false; _lastPos = default; }
                bool moved = haveGap && _lastPos != (x, y, w, h);
                // always reassert topmost; move/size only when the target changed
                uint flags = Native.SWP_NOACTIVATE | (moved && !_dragging ? 0u : Native.SWP_NOMOVE | Native.SWP_NOSIZE);
                if (_menuOpen) flags |= Native.SWP_NOZORDER; // menu/popup open: do not raise the widget over it
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, flags);
                if (moved && !_dragging) _lastPos = (x, y, w, h);
            }
            string modeTxt = _dragMode ? "drag" : _settings.Mode;
            Info.Text = $"mode={modeTxt} off={_settings.OffsetFromRight?.ToString() ?? "-"} gap={gapTxt}\nDPI={dpi} ({scale * 100:F0}%) rata={_align} fs={(fs ? "ya" : "tidak")} ah={(_autoHide ? "ya" : "tidak")} last={_lastReason}";

            var posLog = $"mode={_settings.Mode}/{(_dragMode ? "drag" : "-")} off={_settings.OffsetFromRight} align={_align} dpi={dpi} trayLeft={res?.TrayLeft} win=({x},{y},{x + w},{y + h}) visible={wantVisible} gaps={gapTxt}";
            if (posLog != _lastPosLog)
            {
                _lastPosLog = posLog;
                Taskbar.Log($"{Now()} layout: {posLog} {res?.Note}");
            }
        }
        catch (Exception ex)
        {
            Taskbar.Log($"{Now()} Reassert({reason}) failed: {ex}");
            Info.Text = "error: " + ex.Message;
        }
        finally { _inReassert = false; }
    }
}
