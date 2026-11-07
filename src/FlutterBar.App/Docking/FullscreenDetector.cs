using FlutterBar.App.Diagnostics;
using FlutterBar.App.Native;
using FlutterBar.Core.Docking;

namespace FlutterBar.App.Docking;

/// <summary>
/// Win32 glue for <see cref="FullscreenRule"/>: the foreground window (classified as shell by its owning process,
/// <see cref="ShellWindowRules"/>), its monitor versus the taskbar's monitor, and <c>SHQueryUserNotificationState</c>
/// (QUNS_BUSY / QUNS_RUNNING_D3D_FULL_SCREEN). UI thread only; cheap (no UIA).
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
        var onTaskbarMonitor = monitorRect is not null && monitor == NativeMethods.MonitorFromWindow(tray, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return new ForegroundInfo(false, shell, onTaskbarMonitor, NativeMethods.WindowBox(fg) ?? default, monitorRect ?? default);
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
