using System.Globalization;
using StatefulBar.Core.Layout;

namespace StatefulBar.Core.Docking;

/// <summary>A taskbar the widget can dock to: the monitor it is on (device name such as <c>\\.\DISPLAY2</c> and rect).</summary>
public sealed record TaskbarCandidate(string Device, bool IsPrimary, Box Monitor);

/// <summary>One "Show on ▸" menu entry; <paramref name="Value"/> is what <c>widget.monitor</c> stores (null = main monitor).</summary>
public sealed record TaskbarMenuItem(string? Value, string Label, bool IsCurrent);

/// <summary>
/// Which taskbar the widget docks to (spec §7.3, round 2 batch 6b). Pure: the App lists the real taskbars
/// (<c>Shell_TrayWnd</c> + each <c>Shell_SecondaryTrayWnd</c>) and their monitors.
/// </summary>
public static class TaskbarTargets
{
    /// <summary>
    /// Index of the taskbar on monitor <paramref name="wanted"/> (device name, case-insensitive); when it is gone (monitor
    /// unplugged, "Show my taskbar on all displays" off) or null: the main taskbar, else the first. -1 when the list is empty.
    /// </summary>
    public static int Pick(IReadOnlyList<TaskbarCandidate> taskbars, string? wanted)
    {
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            for (var i = 0; i < taskbars.Count; i++)
            {
                if (string.Equals(taskbars[i].Device, wanted.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }
        for (var i = 0; i < taskbars.Count; i++)
        {
            if (taskbars[i].IsPrimary)
            {
                return i;
            }
        }
        return taskbars.Count > 0 ? 0 : -1;
    }

    /// <summary>Menu entries left to right (then top to bottom): "Monitor N (main) W×H"; the main monitor's value is null.</summary>
    public static IReadOnlyList<TaskbarMenuItem> MenuItems(IReadOnlyList<TaskbarCandidate> taskbars, string? wanted)
    {
        var current = Pick(taskbars, wanted);
        var currentDevice = current >= 0 ? taskbars[current].Device : null;
        return taskbars
            .OrderBy(t => t.Monitor.Left)
            .ThenBy(t => t.Monitor.Top)
            .Select((t, i) => new TaskbarMenuItem(
                t.IsPrimary ? null : t.Device,
                string.Create(CultureInfo.InvariantCulture, $"Monitor {i + 1}{(t.IsPrimary ? " (main)" : string.Empty)}  {t.Monitor.Right - t.Monitor.Left:F0}×{t.Monitor.Bottom - t.Monitor.Top:F0}"),
                string.Equals(t.Device, currentDevice, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
