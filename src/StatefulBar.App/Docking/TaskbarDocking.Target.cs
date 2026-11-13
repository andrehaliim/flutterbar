using StatefulBar.App.Diagnostics;
using StatefulBar.App.Native;
using StatefulBar.Core.Layout;

namespace StatefulBar.App.Docking;

/// <summary>
/// Which taskbar the widget docks to (spec §7.3, round 2 batch 6b): the one on <see cref="TargetMonitor"/>, else the main
/// one. Resolved on every reassert, so a monitor that comes or goes, or an explorer restart, is followed within one tick.
/// Moving to another taskbar starts the layout over; on a secondary taskbar (no <c>TrayNotifyWnd</c> to fall back on) the
/// widget waits for the first UIA layout, at most <see cref="SecondaryLayoutWait"/>, instead of covering the clock.
/// </summary>
internal sealed partial class TaskbarDocking
{
    private static readonly TimeSpan SecondaryLayoutWait = TimeSpan.FromSeconds(3);

    private string? _targetMonitor;
    private IntPtr _targetHwnd;
    private bool _onSecondary;
    private Box? _targetMonitorRect;
    private DateTime _targetSince = DateTime.UtcNow;

    /// <summary><c>widget.monitor</c>: device name of the monitor whose taskbar to use; null = the main taskbar.</summary>
    public string? TargetMonitor
    {
        get => _targetMonitor;
        set
        {
            if (string.Equals(_targetMonitor, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _targetMonitor = value;
            AppLog.Write($"taskbar target wanted inst={_id}: {value ?? "main"}");
            RequestReassert("target");
        }
    }

    /// <summary>The taskbar to dock to right now (zero when there is none); a different one than before resets the layout.</summary>
    private IntPtr ResolveTray()
    {
        if (TaskbarWindows.Resolve(_targetMonitor) is not { } target)
        {
            return IntPtr.Zero;
        }
        _targetMonitorRect = target.Info.Monitor;
        if (target.Hwnd != _targetHwnd)
        {
            var moved = _targetHwnd != IntPtr.Zero;
            _targetHwnd = target.Hwnd;
            _onSecondary = !target.Info.IsPrimary;
            _targetSince = DateTime.UtcNow;
            if (moved)
            {
                ResetLayoutForNewTaskbar();
            }
            var fallback = _targetMonitor is not null && !string.Equals(target.Info.Device, _targetMonitor, StringComparison.OrdinalIgnoreCase);
            var m = target.Info.Monitor;
            AppLog.Write(
                $"taskbar target inst={_id}: {(_onSecondary ? "secondary" : "main")} 0x{target.Hwnd:X} on {target.Info.Device} ({m.Left:F0},{m.Top:F0},{m.Right:F0},{m.Bottom:F0})"
                + (fallback ? $" (wanted {_targetMonitor} has no taskbar: main used)" : string.Empty));
        }
        return target.Hwnd;
    }

    // Results of the old taskbar (layout, debounce state, stable X) mean nothing on the new one.
    private void ResetLayoutForNewTaskbar()
    {
        _layoutGeneration++;
        _layout = null;
        _evaluatedLayout = null;
        _lastValidX = null;
        _debouncer.Reset();
    }

    /// <summary>On a secondary taskbar without a UIA layout yet: stay hidden for a moment rather than cover the clock.</summary>
    private bool WaitingForSecondaryLayout() => _onSecondary && DateTime.UtcNow - _targetSince < SecondaryLayoutWait;

    /// <summary>
    /// Rect auto-hide visibility is measured against: the primary monitor for the main taskbar (v3.2: not the monitor of
    /// the hidden taskbar HWND), the target's monitor for a secondary one.
    /// </summary>
    private Box? AutoHideMonitor() => _onSecondary ? _targetMonitorRect : NativeMethods.PrimaryMonitorRect();
}
