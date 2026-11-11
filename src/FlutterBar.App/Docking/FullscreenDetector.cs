using FlutterBar.App.Diagnostics;
using FlutterBar.App.Native;
using FlutterBar.Core.Docking;

namespace FlutterBar.App.Docking;

/// <summary>
/// Win32 glue for <see cref="FullscreenRule"/>: the foreground window (classified as shell by its owning process,
/// <see cref="ShellWindowRules"/>), its monitor versus the taskbar's monitor, and <c>SHQueryUserNotificationState</c>
/// (QUNS_BUSY / QUNS_RUNNING_D3D_FULL_SCREEN). While the foreground is on another monitor or is the desktop, the topmost
/// application window on the taskbar's monitor is checked instead (z-order walk). UI thread only; cheap (no UIA).
/// </summary>
internal sealed class FullscreenDetector
{
    private int _lastState = -1;

    public bool IsFullscreen { get; private set; }

    /// <summary>Re-evaluates; returns true when <see cref="IsFullscreen"/> changed.</summary>
    public bool Update(IntPtr ownHwnd, IntPtr tray, bool autoHide)
    {
        var state = QueryNotificationState();
        var foreground = Foreground(ownHwnd, tray);
        var now = FullscreenRule.Evaluate(autoHide, foreground, state, IsFullscreen);
        if (state != _lastState)
        {
            _lastState = state;
            AppLog.Write($"SHQueryUserNotificationState={state} fullscreen={now} autoHide={autoHide}");
        }
        var changed = now != IsFullscreen;
        IsFullscreen = now;
        return changed;
    }

    private static ForegroundInfo Foreground(IntPtr ownHwnd, IntPtr tray)
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ownHwnd)
        {
            return new ForegroundInfo(true, false, false, default, default);
        }
        var className = NativeMethods.ClassOf(fg);
        var shell = ShellWindowRules.IsShell(className, ProcessNames.OfWindow(fg));
        var monitor = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorRect = NativeMethods.MonitorRect(monitor);
        // unknown monitor rect -> "not on the taskbar monitor" (never hide on a failed lookup)
        var trayMonitor = NativeMethods.MonitorFromWindow(tray, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var onTaskbarMonitor = monitorRect is not null && monitor == trayMonitor;
        // the desktop is one window across all monitors: its monitor is not where the click landed
        var desktop = ShellWindowRules.IsDesktop(className);
        // focus on another monitor or on the desktop: a fullscreen app may still cover the taskbar's monitor
        var covered = (!onTaskbarMonitor || desktop) && TopWindowCoversMonitor(trayMonitor);
        return new ForegroundInfo(
            false, shell, onTaskbarMonitor, NativeMethods.WindowBox(fg) ?? default, monitorRect ?? default, covered, desktop);
    }

    /// <summary>
    /// True when the topmost application window on <paramref name="monitor"/> (z-order; skipping our own windows, shell,
    /// hidden, minimized, cloaked and tool windows) covers the whole monitor. False on any failed lookup (never hide by mistake).
    /// </summary>
    private static bool TopWindowCoversMonitor(IntPtr monitor)
    {
        if (NativeMethods.MonitorRect(monitor) is not { } monitorRect)
        {
            return false;
        }
        var covers = false;
        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)
                    || NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST) != monitor
                    || ((long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOOLWINDOW) != 0
                    || NativeMethods.IsCloaked(hwnd))
                {
                    return true;
                }
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == (uint)Environment.ProcessId || ShellWindowRules.IsShell(NativeMethods.ClassOf(hwnd), ProcessNames.OfWindow(hwnd)))
                {
                    return true;
                }
                covers = NativeMethods.WindowBox(hwnd) is { } rect && FullscreenRule.CoversMonitor(rect, monitorRect);
                return false; // first application window found: stop
            },
            IntPtr.Zero);
        return covers;
    }

    private static int QueryNotificationState()
    {
        var hr = NativeMethods.SHQueryUserNotificationState(out var state);
        if (hr != 0)
        {
            AppLog.Limited("quns-fail", $"SHQueryUserNotificationState failed hr=0x{hr:X8} (ignored for fullscreen)", 30);
            return 0;
        }
        return state;
    }
}
