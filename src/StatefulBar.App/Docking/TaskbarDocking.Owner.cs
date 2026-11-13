using System.Runtime.InteropServices;
using StatefulBar.App.Diagnostics;
using StatefulBar.App.Native;

namespace StatefulBar.App.Docking;

/// <summary>
/// Owner relation to the taskbar (spike v2/v3.2 rules) and the TaskbarCreated message. Cross-process owner attaches the
/// input queues of both threads: the UI thread must never block.
/// </summary>
internal sealed partial class TaskbarDocking
{
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_dead && _taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
        {
            AppLog.Write($"TaskbarCreated received inst={_id} (explorer restarted)");
            _layoutGeneration++; // queries started before this are stale
            _layout = null;
            _evaluatedLayout = null;
            _debouncer.ArmImmediate();
            RequestReassert("TaskbarCreated");
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Owner = the target taskbar (<c>Shell_TrayWnd</c> or a <c>Shell_SecondaryTrayWnd</c>) via GWLP_HWNDPARENT, verified with GetWindowLongPtr (NOT GetWindow(GW_OWNER), which is 0
    /// while Start/Search is open). Never re-set an owner that is already the tray: that CLEARS it (v3.1 bug). Failures
    /// on timer ticks are counted; 3 in a row recreate the window (see <see cref="OnTimer"/>).
    /// </summary>
    private void EnsureOwner(IntPtr tray, string reason, bool timerTick)
    {
        if (tray != _tray || _locationHook == IntPtr.Zero)
        {
            InstallLocationHook(tray, reason);
            _tray = tray;
            _trayNotify = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        }
        var before = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT);
        if (before == tray)
        {
            _ownerFails = 0;
            return;
        }
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, tray);
        var error = Marshal.GetLastWin32Error();
        var after = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT);
        if (after == tray)
        {
            _ownerFails = 0;
            AppLog.Write($"owner attached ({reason}) inst={_id} hwnd=0x{_hwnd:X}: tray=0x{tray:X} verified (before=0x{before:X})");
            return;
        }
        if (timerTick)
        {
            _ownerFails++;
        }
        AppLog.Limited(
            "owner-fail",
            $"FAILED to set owner ({reason}) inst={_id} hwnd=0x{_hwnd:X}: tray=0x{tray:X} owner=0x{after:X} before=0x{before:X} lastError={error} consecutiveFails={_ownerFails}");
    }

    private void InstallLocationHook(IntPtr tray, string reason)
    {
        if (_locationHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }
        NativeMethods.GetWindowThreadProcessId(tray, out var pid);
        _locationHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _locationCallback, pid, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        // a failed hook is retried on the next reassert (the 600 ms timer still covers placement), so rate-limit the log
        AppLog.Limited("location-hook", $"location hook ({reason}) inst={_id}: tray=0x{tray:X} pid={pid} {(_locationHook == IntPtr.Zero ? "FAILED" : "ok")}", 10);
    }
}
