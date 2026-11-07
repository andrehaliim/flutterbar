using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DockingSpike;

public partial class MainWindow : Window
{
    const int Gap = 8;
    static readonly HashSet<string> ShellClasses = new() { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW" };
    static readonly HashSet<string> ShellHostedClasses = new() { "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow" };
    static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "ShellHost" };

    IntPtr _hwnd, _tray, _fgHook, _locHook;
    uint _taskbarCreatedMsg;
    Native.WinEventDelegate? _fgCb, _locCb; // keep alive (GC)
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly Dictionary<string, DateTime> _lastLog = new();
    bool _locPending, _queryRunning, _hidden, _fullscreen;
    DateTime _lastQuery = DateTime.MinValue;
    Taskbar.Result? _layout;
    string _lastReason = "-";
    string _lastPosLog = "";
    int _qunsLogged = -1;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        _timer.Tick += (_, _) => Reassert("timer");
        Closed += OnClosed;
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _fgCb = OnForeground;
        _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgCb, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        if (_fgHook == IntPtr.Zero) Taskbar.Log("SetWinEventHook(FOREGROUND) failed");
        _locCb = OnLocation;
        AttachToTaskbar("startup");
        _timer.Start();
        Reassert("startup");
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            Taskbar.Log($"{Now()} TaskbarCreated received (explorer restarted)");
            _layout = null;
            AttachToTaskbar("TaskbarCreated");
            Reassert("TaskbarCreated");
        }
        return IntPtr.Zero;
    }

    // (Re)find the taskbar, set it as owner, and (re)install the location hook for its process.
    void AttachToTaskbar(string why)
    {
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) { Taskbar.Log($"{Now()} AttachToTaskbar({why}): Shell_TrayWnd not found"); return; }
        if (_locHook != IntPtr.Zero) { Native.UnhookWinEvent(_locHook); _locHook = IntPtr.Zero; }
        _tray = tray;
        Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT, tray);
        var owner = Native.GetWindow(_hwnd, Native.GW_OWNER);
        uint tid = Native.GetWindowThreadProcessId(tray, out var pid);
        _locHook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _locCb!, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
        Taskbar.Log($"{Now()} AttachToTaskbar({why}): tray=0x{tray:X} owner=0x{owner:X} pid={pid} tid={tid} locHook={(_locHook == IntPtr.Zero ? "FAILED" : "ok")}");
    }

    void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_fgHook != IntPtr.Zero) Native.UnhookWinEvent(_fgHook);
        if (_locHook != IntPtr.Zero) Native.UnhookWinEvent(_locHook);
        _fgHook = _locHook = IntPtr.Zero;
        if (Program.Exiting) return;
        // Destroyed together with its owner (explorer restart): recreate.
        Taskbar.Log($"{Now()} widget window destroyed unexpectedly (owner gone?) -> recreating");
        Dispatcher.BeginInvoke(() => new MainWindow().Show());
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
        if (_lastLog.TryGetValue(key, out var t) && (DateTime.Now - t).TotalSeconds < 2) return;
        _lastLog[key] = DateTime.Now;
        Taskbar.Log($"{Now()} reassert: {reason}");
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

    bool IsShellForeground(IntPtr fg, string cls)
    {
        if (ShellClasses.Contains(cls)) return true;
        if (!ShellHostedClasses.Contains(cls)) return false;
        try
        {
            Native.GetWindowThreadProcessId(fg, out var pid);
            using var p = Process.GetProcessById((int)pid);
            return ShellProcesses.Contains(p.ProcessName);
        }
        catch (Exception ex)
        {
            Taskbar.Log($"{Now()} process lookup failed for class {cls}: {ex.Message} (treated as shell)");
            return true;
        }
    }

    bool DetectFullscreen(IntPtr tray, out string detail)
    {
        detail = "";
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return _fullscreen && fg == _hwnd; // keep state when we are foreground
        var cls = Native.ClassOf(fg);
        if (IsShellForeground(fg, cls)) return false;
        var mon = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
        if (mon != Native.MonitorFromWindow(tray, Native.MONITOR_DEFAULTTONEAREST)) return false;
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi) || !Native.GetWindowRect(fg, out var r)) return false;
        bool covers = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        detail = $"fg={cls}";
        return covers;
    }

    void Reassert(string reason)
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            _lastReason = reason;
            LogReason(reason);
            var tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) { Info.Text = "taskbar not found"; return; }
            if (tray != _tray || Native.GetWindow(_hwnd, Native.GW_OWNER) != tray) AttachToTaskbar("handle/owner changed");
            if (reason == "timer") KickQuery(tray);

            // fullscreen: only meaningful when taskbar auto-hide is off
            bool autoHide = IsAutoHide();
            bool fs = !autoHide && DetectFullscreen(tray, out _);
            if (reason == "timer")
            {
                Native.SHQueryUserNotificationState(out var quns);
                if (quns != _qunsLogged) { _qunsLogged = quns; Taskbar.Log($"{Now()} SHQueryUserNotificationState={quns} fullscreenRule={fs} autoHide={autoHide}"); }
            }
            if (fs != _fullscreen) { _fullscreen = fs; Taskbar.Log($"{Now()} fullscreen -> {(fs ? "YES (hide widget)" : "NO (show widget)")} [{reason}]"); }

            uint dpi = Native.GetDpiForWindow(_hwnd);
            double scale = dpi / 96.0;
            int w = (int)(300 * scale), h = (int)(40 * scale);
            var res = _layout;
            string align = ReadAlignment();
            int free = 0, x = 0, y = 0;
            string rectTxt = "?";
            if (res is not null)
            {
                int freeLeft = res.ButtonsRight + Gap, freeRight = res.TrayLeft - Gap;
                free = freeRight - freeLeft;
                x = freeRight - w;
                y = res.Taskbar.Top + (res.Taskbar.Bottom - res.Taskbar.Top - h) / 2;
                rectTxt = $"({x},{y},{x + w},{y + h})";
            }
            if (fs)
            {
                if (!_hidden) { Native.ShowWindow(_hwnd, 0); _hidden = true; }
            }
            else
            {
                uint flags = Native.SWP_NOACTIVATE | (_hidden ? Native.SWP_SHOWWINDOW : 0);
                if (res is null) flags |= Native.SWP_NOMOVE | Native.SWP_NOSIZE;
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, flags);
                _hidden = false;
            }
            Info.Text = $"free={free}px DPI={dpi} ({scale * 100:F0}%) rata={align}\nfullscreen={(fs ? "ya" : "tidak")} autohide={(autoHide ? "ya" : "tidak")} last={_lastReason}";

            var posLog = $"align={align} free={free} dpi={dpi} occRight={res?.ButtonsRight} trayLeft={res?.TrayLeft} win={rectTxt} fs={fs}";
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
    }
}
