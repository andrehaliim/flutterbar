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

    readonly string _id = Guid.NewGuid().ToString("N")[..6];
    int _ownerFails;
    bool _abHidden;
    HwndSource? _src;
    IntPtr _hwnd, _tray, _fgHook, _locHook;
    uint _taskbarCreatedMsg;
    Native.WinEventDelegate? _fgCb, _locCb; // keep alive (GC)
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly Dictionary<string, DateTime> _lastLog = new();
    readonly Dictionary<uint, string> _procNames = new();
    bool _locPending, _queryRunning, _fullscreen, _inReassert, _dead, _noFit;
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
    // v3.1 placement state (auto mode): sizes Full 300 / Compact 260 / ButtonsOnly 120 (logical px), debounced by 2 consecutive UIA queries
    static readonly int[] SizeW = { 300, 260, 120 };
    static readonly string[] SizeName = { "Full", "Compact", "ButtonsOnly" };
    record Placement(int Size, int X, bool NoFit);
    Placement? _applied, _pending;
    int _pendingHeld;
    string? _toolTipText;
    int? _lastValidX;
    bool _immediate = true;
    Taskbar.Result? _evalLayout;

    public MainWindow()
    {
        InitializeComponent();
        Left = -32000; Top = -32000; // no flash at the default position before the first placement
        Program.FirstInstanceId ??= _id;
        SourceInitialized += OnSourceInitialized;
        _timer.Tick += OnTick;
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

    void OnDebugToggle(object sender, RoutedEventArgs e)
    {
        _settings.HideDebug = DebugItem.IsChecked;
        _settings.Save();
        Taskbar.Log($"{Now()} hide debug info -> {_settings.HideDebug}");
        Reassert("debug-toggle");
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
                _settings.Mode = "manual"; _settings.OffsetFromRight = Math.Max(0, tb.Right - (dx + w));
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
        _settings.Mode = "auto"; _settings.OffsetFromRight = null;
        _settings.Save(); // keeps HideDebug; position back to auto
        _lastPos = default;
        _applied = null; _pending = null; _pendingHeld = 0; _evalLayout = null; _immediate = true; // next auto layout applies immediately
        Taskbar.Log($"{Now()} reset to auto position (drag mode exited, immediate-apply armed)");
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

    void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Taskbar.Log($"{Now()} drag ended by LostMouseCapture, keeping x={_dragX}");
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
        _src = HwndSource.FromHwnd(_hwnd);
        _src?.AddHook(WndProc);
        DebugItem.IsChecked = _settings.HideDebug;
        Taskbar.Log($"{Now()} window created inst={_id} hwnd=0x{_hwnd:X} hideDebug={_settings.HideDebug}");
        _fgCb = OnForeground;
        _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgCb, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        if (_fgHook == IntPtr.Zero) Taskbar.Log("SetWinEventHook(FOREGROUND) failed");
        _locCb = OnLocation;
        _autoHide = IsAutoHide();
        _align = ReadAlignment();
        AttachToTaskbar("startup", true);
        // SourceInitialized fires inside Show() BEFORE WPF's own ShowWindow(SW_SHOWNA): the first Reassert must run after the window is really shown,
        // otherwise a hide decision (auto-hide/fullscreen at startup or recreate) is overridden by WPF's show.
        Dispatcher.BeginInvoke(() =>
        {
            if (_dead) return;
            _timer.Start();
            Reassert("startup");
        }, DispatcherPriority.Loaded);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_dead && _taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            Taskbar.Log($"{Now()} TaskbarCreated received (explorer restarted)");
            _layout = null; _immediate = true; _evalLayout = null;
            AttachToTaskbar("TaskbarCreated", true);
            Reassert("TaskbarCreated");
        }
        return IntPtr.Zero;
    }

    // Set the taskbar as owner; (re)install the location hook only when the tray handle changed (or forced).
    // The owner is verified with GetWindowLongPtr(GWLP_HWNDPARENT), NOT GetWindow(GW_OWNER): while the Start/Search
    // flyout (an immersive CoreWindow) is foreground, GW_OWNER returns 0 although the real owner is intact, and
    // re-setting the same owner then CLEARS it for good (v3.1 bug, see findings doc).
    void AttachToTaskbar(string why, bool forceHook)
    {
        if (_dead) return;
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) { LogLimited("attach-notfound", $"{Now()} AttachToTaskbar({why}) inst={_id}: Shell_TrayWnd not found"); return; }
        bool changed = tray != _tray;
        if (changed || forceHook || _locHook == IntPtr.Zero)
        {
            if (_locHook != IntPtr.Zero) { Native.UnhookWinEvent(_locHook); _locHook = IntPtr.Zero; }
            uint tid = Native.GetWindowThreadProcessId(tray, out var pid);
            _locHook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _locCb!, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
            Taskbar.Log($"{Now()} location hook ({why}) inst={_id}: tray=0x{tray:X} pid={pid} tid={tid} locHook={(_locHook == IntPtr.Zero ? "FAILED" : "ok")}");
        }
        _tray = tray;
        var before = Native.GetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT);
        int err = 0;
        if (before != tray && !(Program.SimulateOwnerFailure && _id == Program.FirstInstanceId))
        {
            Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT, tray);
            err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }
        var owner = Native.GetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT);
        var gwOwner = Native.GetWindow(_hwnd, Native.GW_OWNER);
        if (owner == tray)
        {
            _ownerFails = 0;
            LogLimited("attach", $"{Now()} AttachToTaskbar({why}) inst={_id} hwnd=0x{_hwnd:X}: tray=0x{tray:X} owner=0x{owner:X} verified (before=0x{before:X}, GW_OWNER=0x{gwOwner:X})");
        }
        else
        {
            if (_lastReason == "timer") _ownerFails++; // count timer ticks only (>= 600 ms apart)
            LogLimited("attach", $"{Now()} AttachToTaskbar({why}) inst={_id} hwnd=0x{_hwnd:X}: FAILED to set owner: tray=0x{tray:X} owner=0x{owner:X} before=0x{before:X} lastError={err} consecutiveFails={_ownerFails}");
        }
    }

    void LogLimited(string key, string msg)
    {
        if (_lastLog.TryGetValue(key, out var t) && (DateTime.Now - t).TotalSeconds < 2) return;
        _lastLog[key] = DateTime.Now;
        Taskbar.Log(msg);
    }

    // Full stop of this instance: timer, WinEvent hooks, window-proc hook. After this no handler does anything (all check _dead).
    void Teardown()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        if (_fgHook != IntPtr.Zero) Native.UnhookWinEvent(_fgHook);
        if (_locHook != IntPtr.Zero) Native.UnhookWinEvent(_locHook);
        _fgHook = _locHook = IntPtr.Zero;
        _src?.RemoveHook(WndProc);
        _src = null;
    }

    void Recreate(string why)
    {
        if (_dead) return;
        _dead = true;
        if (_dragMode || _dragging)
        {
            Taskbar.Log($"{Now()} inst={_id} recreate while drag mode active: ending drag, unsaved drag state dropped (x={_dragX})");
            _dragMode = false; _dragging = false; _dragX = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }
        Teardown();
        if (Program.Exiting) return;
        Taskbar.Log($"{Now()} widget window destroyed inst={_id} ({why}) -> old instance stopped, recreating");
        var oldHwnd = _hwnd;
        Dispatcher.BeginInvoke(() =>
        {
            new MainWindow().Show();
            if (Native.IsWindow(oldHwnd)) Close(); // OnClosed -> Recreate is a no-op (_dead)
        });
    }

    void OnClosed(object? sender, EventArgs e) => Recreate("Closed event, owner gone?");

    void OnTick(object? sender, EventArgs e) => OnTimer();

    void OnTimer()
    {
        if (_dead) return;
        if (_hwnd != IntPtr.Zero && !Native.IsWindow(_hwnd)) { Recreate("IsWindow=false"); return; }
        if (_ownerFails >= 3)
        {
            if ((DateTime.Now - Program.LastOwnerRecreate).TotalSeconds < 20) LogLimited("owner-recreate-limit", $"{Now()} inst={_id} owner attach keeps failing ({_ownerFails}x); recreate rate-limited");
            else { Program.LastOwnerRecreate = DateTime.Now; Program.SimulateOwnerFailure = false; Recreate($"owner attach failed {_ownerFails}x"); return; }
        }
        if (_menuOpen && ContextMenu is { IsOpen: false })
        {
            _menuOpen = false;
            Taskbar.Log($"{Now()} failsafe: _menuOpen was set but ContextMenu.IsOpen=false -> cleared");
        }
        _autoHide = IsAutoHide();
        _align = ReadAlignment();
        Reassert("timer");
    }

    void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_dead) return;
        Reassert("foreground:" + Native.ClassOf(hwnd));
    }

    void OnLocation(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_dead || idObject != Native.OBJID_WINDOW || _locPending) return;
        _locPending = true;
        Dispatcher.BeginInvoke(() => { _locPending = false; if (!_dead) Reassert("locationchange"); }, DispatcherPriority.Background);
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
                if (r is not null && !_dead && !_abHidden) _layout = r;
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

    // Pick the largest size that fits a gap (Full -> Compact -> ButtonsOnly; rightmost fitting gap per size). Nothing fits: ButtonsOnly at the last valid X.
    Placement Choose(List<(int L, int R)> gaps, double scale, IntPtr tray, Native.RECT tbr)
    {
        for (int i = 0; i < SizeW.Length; i++)
        {
            int ws = (int)(SizeW[i] * scale);
            for (int g = gaps.Count - 1; g >= 0; g--)
                if (gaps[g].R - gaps[g].L >= ws) return new Placement(i, gaps[g].R - ws, false);
        }
        int wb = (int)(SizeW[2] * scale);
        int x;
        if (_lastValidX is int lv) x = lv;
        else
        {
            var tn = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            x = tn != IntPtr.Zero && Native.GetWindowRect(tn, out var tnr) && tnr.Left > tbr.Left ? tnr.Left - Gap - wb : tbr.Right - wb;
        }
        return new Placement(2, Math.Clamp(x, tbr.Left, Math.Max(tbr.Left, tbr.Right - wb)), true);
    }

    static bool SamePlacement(Placement a, Placement b) => a.Size == b.Size && a.NoFit == b.NoFit && Math.Abs(a.X - b.X) <= 3;

    // Called once per new UIA layout. A different placement is applied only after 2 consecutive queries agree (immediate on startup/reset).
    void Evaluate(List<(int L, int R)> gaps, double scale, IntPtr tray, Native.RECT tbr, string gapTxt)
    {
        var cand = Choose(gaps, scale, tray, tbr);
        string desc = $"{SizeName[cand.Size]} x={cand.X}{(cand.NoFit ? " NOFIT" : "")}";
        if (_applied is null || _immediate)
        {
            Apply(cand, $"immediate ({(_applied is null ? "first layout" : "after startup/reset")})", gapTxt);
        }
        else if (SamePlacement(cand, _applied)) { _pending = null; _pendingHeld = 0; }
        else if (_pending is not null && SamePlacement(cand, _pending)) Apply(cand, "confirmed by 2 consecutive queries", gapTxt);
        else if (_pendingHeld >= 5) Apply(cand, $"max-hold: differing candidates held back {_pendingHeld} queries, applying the latest", gapTxt);
        else
        {
            _pending = cand; _pendingHeld++;
            Taskbar.Log($"{Now()} placement candidate {desc} held back (applied {SizeName[_applied.Size]} x={_applied.X}); needs 2 consecutive queries; gaps: {gapTxt}");
        }
    }

    void Apply(Placement cand, string why, string gapTxt)
    {
        _applied = cand; _pending = null; _pendingHeld = 0; _immediate = false;
        if (!cand.NoFit) _lastValidX = cand.X;
        Taskbar.Log($"{Now()} placement APPLIED {SizeName[cand.Size]} x={cand.X}{(cand.NoFit ? " NOFIT (stay visible, hint)" : "")} [{why}] gaps: {gapTxt}");
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
            if (tray != _tray || Native.GetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT) != tray) AttachToTaskbar("handle/owner changed", false);
            else _ownerFails = 0;
            bool timer = reason == "timer";

            // v3.2 auto-hide tracking: visible fraction of the taskbar rect on its monitor; < 50% = hidden by auto-hide.
            Native.GetWindowRect(tray, out var tbr);
            bool abHidden = false;
            string abTxt = "";
            if (_autoHide)
            {
                var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
                if (Native.GetMonitorInfo(Native.MonitorFromPoint(new Native.POINT { X = 0, Y = 0 }, Native.MONITOR_DEFAULTTOPRIMARY), ref mi))
                {
                    long tbArea = Math.Max(1L, (long)(tbr.Right - tbr.Left) * (tbr.Bottom - tbr.Top));
                    long vw = Math.Max(0, Math.Min(tbr.Right, mi.rcMonitor.Right) - Math.Max(tbr.Left, mi.rcMonitor.Left));
                    long vh = Math.Max(0, Math.Min(tbr.Bottom, mi.rcMonitor.Bottom) - Math.Max(tbr.Top, mi.rcMonitor.Top));
                    double frac = (double)(vw * vh) / tbArea;
                    abHidden = frac < 0.5;
                    abTxt = $"vis={frac * 100:F0}%";
                }
                else LogLimited("ab-monitor", $"{Now()} GetMonitorInfo failed for taskbar (auto-hide tracking skipped), err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }
            if (abHidden != _abHidden)
            {
                _abHidden = abHidden;
                Taskbar.Log($"{Now()} auto-hide: taskbar {(abHidden ? "OFF-SCREEN -> hide widget, UIA re-layout paused" : "VISIBLE -> show widget, follow taskbar rect")} {abTxt} tbRect=({tbr.Left},{tbr.Top},{tbr.Right},{tbr.Bottom}) [{reason}]");
            }
            if (timer && !_abHidden) KickQuery(tray); // no UIA re-layout while the taskbar is auto-hidden (it would only be REJECTED)

            bool fs = !_autoHide && DetectFullscreen(tray);
            if (timer)
            {
                Native.SHQueryUserNotificationState(out var quns);
                if (quns != _qunsLogged) { _qunsLogged = quns; Taskbar.Log($"{Now()} SHQueryUserNotificationState={quns} fullscreenRule={fs} autoHide={_autoHide}"); }
            }
            if (fs != _fullscreen) { _fullscreen = fs; Taskbar.Log($"{Now()} fullscreen -> {(fs ? "YES (hide widget)" : "NO (show widget)")} [{reason}]"); }

            uint dpi = Native.GetDpiForWindow(_hwnd);
            double scale = dpi / 96.0;
            int sizeIdx = 0;
            int w = (int)(SizeW[0] * scale), h = (int)(40 * scale);
            var res = _layout;
            int x = 0, y = 0;
            string gapTxt = "?";
            bool haveGap = false, noFit = false;
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
                if (!ReferenceEquals(res, _evalLayout) && !_abHidden)
                {
                    _evalLayout = res;
                    Evaluate(gaps, scale, tray, tbr, gapTxt);
                }
                if (_applied is { } ap)
                {
                    sizeIdx = ap.Size; noFit = ap.NoFit;
                    w = (int)(SizeW[sizeIdx] * scale);
                    x = Math.Clamp(ap.X, tbr.Left, Math.Max(tbr.Left, tbr.Right - w));
                    y = yDock; // live taskbar rect, so the widget slides with an auto-hiding bar
                    haveGap = true;
                    gapTxt += $" -> {SizeName[sizeIdx]} x={x}{(noFit ? " (TIDAK MUAT)" : "")}";
                }
            }
            else
            {
                // auto mode, no valid UIA layout yet: dock left of the Win32 TrayNotifyWnd until one arrives
                var tn = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (tn != IntPtr.Zero && Native.GetWindowRect(tn, out var tnr) && tnr.Left > tbr.Left)
                {
                    x = Math.Clamp(tnr.Left - Gap - w, tbr.Left, xMax); y = yDock; haveGap = true;
                    gapTxt = $"(fallback TrayNotifyWnd left={tnr.Left})";
                }
                else gapTxt = "(no layout, no TrayNotifyWnd)";
            }
            if (noFit != _noFit) { _noFit = noFit; Taskbar.Log($"{Now()} taskbar full: {(noFit ? "nothing fits even ButtonsOnly -> widget STAYS VISIBLE (ButtonsOnly, last X) with hint" : "fits again")} gaps: {gapTxt}"); }
            bool wantVisible = !fs && !_abHidden;

            // decide from the ACTUAL window state, not a cached flag (WPF may show the window behind our back)
            bool actuallyVisible = Native.IsWindowVisible(_hwnd);
            if (!wantVisible)
            {
                if (actuallyVisible) Native.ShowWindow(_hwnd, 0);
            }
            else
            {
                if (!actuallyVisible) { Native.ShowWindow(_hwnd, 4); _lastPos = default; }
                bool moved = haveGap && _lastPos != (x, y, w, h);
                // always reassert topmost; move/size only when the target changed
                uint flags = Native.SWP_NOACTIVATE | (moved && !_dragging ? 0u : Native.SWP_NOMOVE | Native.SWP_NOSIZE);
                if (_menuOpen) flags |= Native.SWP_NOZORDER; // menu/popup open: do not raise the widget over it
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, flags);
                if (moved && !_dragging) _lastPos = (x, y, w, h);
            }
            string modeTxt = _dragMode ? "drag" : _settings.Mode;
            bool hideDbg = _settings.HideDebug;
            double fontSize = sizeIdx == 2 ? 9 : (hideDbg ? 13 : 11);
            var margin = sizeIdx == 2 ? new Thickness(3, 0, 3, 0) : new Thickness(5, 1, 5, 1);
            string text;
            if (noFit) text = "▶ ⚡ Taskbar penuh — gunakan Drag widget";
            else if (hideDbg) text = sizeIdx switch { 0 => "▶ ⚡ 🔄 ⏹", 1 => "▶ ⚡ 🔄", _ => "▶ ⚡" };
            else text = sizeIdx switch
            {
                0 => $"[Full] mode={modeTxt} off={_settings.OffsetFromRight?.ToString() ?? "-"} gap={gapTxt}\nDPI={dpi} ({scale * 100:F0}%) rata={_align} fs={(fs ? "ya" : "tidak")} ah={(_autoHide ? "ya" : "tidak")}{(_autoHide ? (_abHidden ? " (hidden)" : " (shown)") : "")} last={_lastReason}",
                1 => $"[Compact] mode={modeTxt} x={x}\nDPI={dpi} rata={_align}",
                _ => "▶ ⚡",
            };
            // assign only on change so the widget text does not flicker/relayout every tick
            if (Info.FontSize != fontSize) Info.FontSize = fontSize;
            if (Info.Margin != margin) Info.Margin = margin;
            if (Info.Text != text) Info.Text = text;
            var tipText = noFit ? "Taskbar penuh \u2014 gunakan Drag widget" : null;
            if (tipText != _toolTipText) { _toolTipText = tipText; ToolTip = tipText; }

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
