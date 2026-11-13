using System.Runtime.InteropServices;
using System.Windows.Automation;
using StatefulBar.App.Diagnostics;
using StatefulBar.App.Native;
using StatefulBar.Core.Docking;
using StatefulBar.Core.Layout;

namespace StatefulBar.App.Docking;

/// <summary>
/// Spike for the widget on a second monitor (round 2 plan, batch 6a). Started with <c>--survey-taskbars</c>: a few seconds
/// after start, writes what FlutterBar can see of EVERY taskbar (<c>Shell_TrayWnd</c> and each <c>Shell_SecondaryTrayWnd</c>)
/// to diag.log as <c>survey:</c> lines: the Win32 window (rect, monitor, DPI, direct children) and its UI Automation subtree
/// with the verdicts of <see cref="TaskbarLayoutAnalyzer"/>. Read-only and one-shot: the docking is not affected.
/// </summary>
internal static class TaskbarSurvey
{
    public const string Argument = "--survey-taskbars";

    private const int MaxDepth = 6; // same as TaskbarProbe
    private const int MaxTaskbars = 16;
    private const int MaxChildren = 32;
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(5); // explorer settled, widget docked

    public static void Start() => _ = Task.Run(async () =>
    {
        await Task.Delay(StartDelay).ConfigureAwait(false);
        try
        {
            Run();
        }
        catch (Exception ex) // diagnostics only: report and stop, never take the app down
        {
            AppLog.Write("survey: failed", ex);
        }
    });

    private static void Run()
    {
        var taskbars = new List<(string Kind, IntPtr Hwnd)>();
        var primary = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero)
        {
            taskbars.Add(("primary", primary));
        }
        var previous = IntPtr.Zero;
        while (taskbars.Count < MaxTaskbars && (previous = NativeMethods.FindWindowEx(IntPtr.Zero, previous, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            taskbars.Add(("secondary", previous));
        }

        AppLog.Write($"survey: {taskbars.Count} taskbar(s) ({taskbars.Count(t => t.Kind == "secondary")} secondary), autoHide={NativeMethods.IsAutoHide()}, primaryMonitor={Format(NativeMethods.PrimaryMonitorRect())}");
        foreach (var (kind, hwnd) in taskbars)
        {
            Describe(kind, hwnd);
        }
        AppLog.Write("survey: done");
    }

    private static void Describe(string kind, IntPtr hwnd)
    {
        var tag = $"survey: [{kind} 0x{hwnd:X}]";
        if (NativeMethods.WindowBox(hwnd) is not { } taskbar)
        {
            AppLog.Write($"{tag} no window rect");
            return;
        }
        var monitor = NativeMethods.MonitorRect(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));
        AppLog.Write($"{tag} rect={Format(taskbar)} monitor={Format(monitor)} dpi={NativeMethods.GetDpiForWindow(hwnd)} visible={NativeMethods.IsWindowVisible(hwnd)}");

        var child = IntPtr.Zero;
        for (var i = 0; i < MaxChildren && (child = NativeMethods.FindWindowEx(hwnd, child, null, null)) != IntPtr.Zero; i++)
        {
            AppLog.Write($"{tag} win32 child class='{NativeMethods.ClassOf(child)}' rect={Format(NativeMethods.WindowBox(child))} visible={NativeMethods.IsWindowVisible(child)}");
        }

        var elements = new List<TaskbarElement>();
        var depths = new List<int>();
        try
        {
            var snapshot = AutomationElement.FromHandle(hwnd).GetUpdatedCache(CreateRequest());
            Collect(snapshot, 0, elements, depths);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        {
            AppLog.Write($"{tag} UIA failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var trayNotifyLeft = NativeMethods.TrayNotifyLeft(hwnd, taskbar);
        var analysis = TaskbarLayoutAnalyzer.Analyze(taskbar, elements, trayNotifyLeft);
        for (var i = 0; i < analysis.Verdicts.Count; i++)
        {
            var (e, verdict) = analysis.Verdicts[i];
            var role = verdict.Role == ElementRole.Occupied ? "COUNTED" : $"{verdict.Role.ToString().ToUpperInvariant()} ({verdict.Reason})";
            AppLog.Write($"{tag} uia d{depths[i]} {role}: class='{e.ClassName}' id='{e.AutomationId}' type={e.ControlType} proc={e.ProcessName} rect={Format(e.Rect)}");
        }
        AppLog.Write($"{tag} analysis valid={analysis.IsValid} reject='{analysis.RejectReason}' trayLeft={analysis.TrayLeft:F0} trayNotifyLeft={trayNotifyLeft?.ToString("F0") ?? "-"} occupied={analysis.Occupied.Count} elements={elements.Count}");
    }

    private static CacheRequest CreateRequest()
    {
        var request = new CacheRequest
        {
            TreeScope = TreeScope.Subtree,
            TreeFilter = new NotCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, Environment.ProcessId)),
            AutomationElementMode = AutomationElementMode.None,
        };
        request.Add(AutomationElement.ClassNameProperty);
        request.Add(AutomationElement.AutomationIdProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.ProcessIdProperty);
        return request;
    }

    private static void Collect(AutomationElement element, int depth, List<TaskbarElement> elements, List<int> depths)
    {
        var cached = element.Cached;
        if (cached.ProcessId == Environment.ProcessId)
        {
            return;
        }
        var rect = cached.BoundingRectangle;
        elements.Add(new TaskbarElement(
            cached.ClassName ?? string.Empty,
            cached.AutomationId ?? string.Empty,
            cached.ControlType?.ProgrammaticName ?? string.Empty,
            ProcessNames.Of((uint)cached.ProcessId) ?? "?",
            rect.IsEmpty ? default : new Box(rect.Left, rect.Top, rect.Right, rect.Bottom)));
        depths.Add(depth);
        if (depth >= MaxDepth)
        {
            return;
        }
        foreach (AutomationElement child in element.CachedChildren)
        {
            Collect(child, depth + 1, elements, depths);
        }
    }

    private static string Format(Box? box) => box is { } b ? $"({b.Left:F0},{b.Top:F0},{b.Right:F0},{b.Bottom:F0})" : "-";
}
