using StatefulBar.App.Native;
using StatefulBar.Core.Docking;

namespace StatefulBar.App.Docking;

/// <summary>A taskbar window and the monitor it is on.</summary>
internal sealed record TaskbarWindow(IntPtr Hwnd, TaskbarCandidate Info);

/// <summary>
/// The taskbars Windows shows right now: <c>Shell_TrayWnd</c> (main) and one <c>Shell_SecondaryTrayWnd</c> per other
/// monitor while "Show my taskbar on all displays" is on (spec §7.3, spike 2026-11-09). Cheap Win32 calls only: safe on
/// the UI thread and on every reassert; handles change when explorer restarts or a monitor comes and goes.
/// </summary>
internal static class TaskbarWindows
{
    private const int MaxSecondary = 16;

    public static IReadOnlyList<TaskbarWindow> All()
    {
        var list = new List<TaskbarWindow>();
        Add(list, NativeMethods.FindWindow("Shell_TrayWnd", null), primary: true);
        var previous = IntPtr.Zero;
        for (var i = 0; i < MaxSecondary && (previous = NativeMethods.FindWindowEx(IntPtr.Zero, previous, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero; i++)
        {
            Add(list, previous, primary: false);
        }
        return list;
    }

    /// <summary>The taskbar on monitor <paramref name="wanted"/> (device name), else the main one; null when there is none.</summary>
    public static TaskbarWindow? Resolve(string? wanted)
    {
        var all = All();
        var index = TaskbarTargets.Pick([.. all.Select(t => t.Info)], wanted);
        return index < 0 ? null : all[index];
    }

    private static void Add(List<TaskbarWindow> list, IntPtr hwnd, bool primary)
    {
        if (hwnd != IntPtr.Zero && NativeMethods.MonitorOf(hwnd) is { } monitor)
        {
            list.Add(new TaskbarWindow(hwnd, new TaskbarCandidate(monitor.Device, primary, monitor.Rect)));
        }
    }
}
