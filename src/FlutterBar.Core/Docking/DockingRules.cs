using FlutterBar.Core.Layout;

namespace FlutterBar.Core.Docking;

/// <summary>v3.2 auto-hide tracking: how much of the live taskbar rect is on the primary monitor.</summary>
public static class AutoHideVisibility
{
    /// <summary>Visible height of <paramref name="bar"/> inside <paramref name="monitor"/> divided by the bar height (0 when not overlapping).</summary>
    public static double VisibleFraction(Box bar, Box monitor)
    {
        var height = bar.Bottom - bar.Top;
        if (height <= 0)
        {
            return 0;
        }
        var overlapWidth = Math.Min(bar.Right, monitor.Right) - Math.Max(bar.Left, monitor.Left);
        if (overlapWidth <= 0)
        {
            return 0;
        }
        var visibleHeight = Math.Max(0, Math.Min(bar.Bottom, monitor.Bottom) - Math.Max(bar.Top, monitor.Top));
        return visibleHeight / height;
    }

    /// <summary>Hidden by auto-hide when less than 50% of the bar height is on screen.</summary>
    public static bool IsHidden(Box bar, Box monitor) => VisibleFraction(bar, monitor) < 0.5;
}

/// <summary>Manual mode (spec §7.3): position stored as the distance from the taskbar's RIGHT edge in physical px.</summary>
public static class ManualPlacement
{
    /// <summary>Upper bound for a stored offset; anything larger (or negative) is treated as corrupt and resets to auto.</summary>
    public const int MaxOffset = 16384;

    public static double XFromOffset(Box taskbar, int offsetFromRight, double width) =>
        ClampX(taskbar, taskbar.Right - offsetFromRight - width, width);

    public static int OffsetFromX(Box taskbar, double x, double width) =>
        Math.Max(0, (int)Math.Round(taskbar.Right - (x + width)));

    /// <summary>Keeps the widget inside the taskbar horizontally; a widget wider than the taskbar sticks to its left edge.</summary>
    public static double ClampX(Box taskbar, double x, double width) =>
        Math.Clamp(x, taskbar.Left, Math.Max(taskbar.Left, taskbar.Right - width));

    public static bool IsValidOffset(int? offset) => offset is >= 0 and <= MaxOffset;
}

/// <param name="IsNullOrSelf">No foreground window, or it is the widget itself: keep the previous decision.</param>
/// <param name="IsShell">Shell window (by class fast path or owning process), e.g. Start, Search, Alt+Tab.</param>
/// <param name="OnTaskbarMonitor">The foreground window is on the monitor that hosts the taskbar.</param>
/// <param name="Rect">Foreground window rect (physical px).</param>
/// <param name="Monitor">That monitor's full rect (<c>rcMonitor</c>).</param>
/// <param name="TaskbarMonitorCovered">
/// Only read when the foreground window is on another monitor or is the desktop: the topmost application window on
/// the taskbar's monitor covers that whole monitor (a fullscreen app that merely lost the focus).
/// </param>
/// <param name="IsDesktop">
/// The foreground window is the desktop (<see cref="ShellWindowRules.IsDesktop"/>). It spans every monitor, so
/// <paramref name="OnTaskbarMonitor"/> does not say where the click landed.
/// </param>
public sealed record ForegroundInfo(
    bool IsNullOrSelf, bool IsShell, bool OnTaskbarMonitor, Box Rect, Box Monitor, bool TaskbarMonitorCovered = false,
    bool IsDesktop = false);

/// <summary>Fullscreen rule from spike v2 plus <c>SHQueryUserNotificationState</c> (spec §7.3).</summary>
public static class FullscreenRule
{
    public const int QunsBusy = 2;
    public const int QunsRunningD3DFullScreen = 3;

    /// <summary>
    /// Only when auto-hide is off; never for shell windows on the taskbar's monitor. Fullscreen when the
    /// foreground window covers the whole monitor, or the notification state is QUNS_BUSY / QUNS_RUNNING_D3D_FULL_SCREEN.
    /// With the foreground on another monitor (shell or not), or on the desktop (one window across all monitors),
    /// the focus says nothing about the taskbar's monitor: <see cref="ForegroundInfo.TaskbarMonitorCovered"/> decides.
    /// </summary>
    public static bool Evaluate(bool autoHide, ForegroundInfo foreground, int notificationState, bool previous)
    {
        ArgumentNullException.ThrowIfNull(foreground);
        if (autoHide)
        {
            return false;
        }
        if (foreground.IsNullOrSelf)
        {
            return previous;
        }
        if (!foreground.OnTaskbarMonitor || foreground.IsDesktop)
        {
            return foreground.TaskbarMonitorCovered;
        }
        if (foreground.IsShell)
        {
            return false;
        }
        return CoversMonitor(foreground.Rect, foreground.Monitor)
            || notificationState is QunsBusy or QunsRunningD3DFullScreen;
    }

    public static bool CoversMonitor(Box window, Box monitor) =>
        window.Left <= monitor.Left && window.Top <= monitor.Top && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;
}

/// <summary>
/// Shell classification by the window's OWNING PROCESS (spike v2); the class list is only a fast path. Covers Alt+Tab /
/// Task View (<c>XamlExplorerHostIslandWindow</c>, owned by explorer), which covers the monitor but is not fullscreen.
/// </summary>
public static class ShellWindowRules
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
    };

    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "SearchHost",
        "StartMenuExperienceHost",
        "ShellExperienceHost",
        "ShellHost",
    };

    /// <param name="processName">Owning process name, or null when the lookup failed (treated as shell: never hide by mistake).</param>
    public static bool IsShell(string className, string? processName) =>
        ShellClasses.Contains(className) || processName is null || ShellProcesses.Contains(processName);

    /// <summary>
    /// The desktop window (<c>Progman</c>, or the <c>WorkerW</c> that hosts the icons). One window across all monitors:
    /// <c>MonitorFromWindow</c> answers with the monitor of the largest overlap, not the one that was clicked.
    /// </summary>
    public static bool IsDesktop(string className) => className is "Progman" or "WorkerW";
}
