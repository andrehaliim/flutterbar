using FlutterBar.Core.Layout;

namespace FlutterBar.Core.Docking;

/// <summary>
/// One UI Automation element of the taskbar tree, flattened to the properties the docking rules need.
/// <paramref name="ControlType"/> is the UIA programmatic name (e.g. <c>ControlType.ToolTip</c>); <paramref name="Rect"/>
/// is in physical pixels (the process is PerMonitorV2 aware, so UIA rects must not be scaled again).
/// </summary>
public sealed record TaskbarElement(string ClassName, string AutomationId, string ControlType, string ProcessName, Box Rect);

public enum ElementRole
{
    /// <summary>Part of the notification area (left edge of the tray).</summary>
    Tray,

    /// <summary>Counted as occupied: the widget must not cover it.</summary>
    Occupied,

    Ignored,
}

public sealed record ElementVerdict(ElementRole Role, string Reason);

/// <summary>Occupied-element rule from spike v3.1 (findings doc "Spike v3.1" item 3, spec §7.3 "Stability").</summary>
public static class TaskbarElementClassifier
{
    public const double RectSlack = 2;
    public const double MinWidth = 8;

    /// <summary>Containers and legacy Win32 children that never occupy space themselves (ReBarWindow32 is wrong on Win11).</summary>
    private static readonly HashSet<string> NotOccupyingClasses = new(StringComparer.Ordinal)
    {
        "Windows.UI.Input.InputSite.WindowClass",
        "Taskbar.TaskbarFrameAutomationPeer",
        "ReBarWindow32",
        "TrayNotifyWnd",
        "Shell_TrayWnd",
    };

    private static readonly HashSet<string> TransientClasses = new(StringComparer.Ordinal)
    {
        "TaskListThumbnailWnd",
        "TaskListOverlayWnd",
        "Xaml_WindowedPopupClass",
        "XamlPopupWindow",
        "ToolTip",
        "tooltips_class32",
        "Popup",
        "Windows.UI.Core.CoreWindow",
    };

    /// <summary>Our own widget processes (the owned widget window shows up in the taskbar UIA tree).</summary>
    private static readonly HashSet<string> WidgetProcesses = new(StringComparer.OrdinalIgnoreCase) { "FlutterBar.App", "DockingSpike" };

    public static bool IsTray(TaskbarElement element) =>
        element.ClassName.StartsWith("SystemTray.", StringComparison.Ordinal)
        || element.AutomationId is "SystemTrayIcon" or "NotifyItemIcon";

    public static bool IsInside(Box rect, Box taskbar) =>
        rect.Left >= taskbar.Left - RectSlack && rect.Right <= taskbar.Right + RectSlack
        && rect.Top >= taskbar.Top - RectSlack && rect.Bottom <= taskbar.Bottom + RectSlack;

    /// <summary>
    /// Counted iff (a) inside the taskbar rect (2 px slack), (b) height &gt;= 50% of the taskbar, (c) width &gt;= 8 px,
    /// (d) not one of our widget processes, (e) not transient (ToolTip control type, popup/thumbnail classes); containers,
    /// images and anything reaching into the tray are ignored too.
    /// </summary>
    public static ElementVerdict Classify(TaskbarElement element, Box taskbar, double trayLeft)
    {
        var r = element.Rect;
        var width = r.Right - r.Left;
        var height = r.Bottom - r.Top;
        var taskbarWidth = taskbar.Right - taskbar.Left;
        var taskbarHeight = taskbar.Bottom - taskbar.Top;

        if (width <= 0 || height <= 0)
        {
            return Ignore("empty rect");
        }
        if (!IsInside(r, taskbar))
        {
            return Ignore("outside taskbar rect");
        }
        if (IsTray(element))
        {
            return new ElementVerdict(ElementRole.Tray, "tray element");
        }
        if (NotOccupyingClasses.Contains(element.ClassName))
        {
            return Ignore("container/legacy window");
        }
        if (width >= taskbarWidth / 2)
        {
            return Ignore("container (>= 50% taskbar width)");
        }
        if (r.Right > trayLeft + 1)
        {
            return Ignore("overlaps tray");
        }
        if (element.ClassName == "Image")
        {
            return Ignore("image");
        }
        if (height < taskbarHeight * 0.5)
        {
            return Ignore("height < 50% taskbar");
        }
        if (width < MinWidth)
        {
            return Ignore("width < 8");
        }
        if (WidgetProcesses.Contains(element.ProcessName))
        {
            return Ignore($"own/other widget process {element.ProcessName}");
        }
        if (element.ControlType == "ControlType.ToolTip" || TransientClasses.Contains(element.ClassName))
        {
            return Ignore("transient (tooltip/popup/thumbnail)");
        }
        return new ElementVerdict(ElementRole.Occupied, "counted");
    }

    private static ElementVerdict Ignore(string reason) => new(ElementRole.Ignored, reason);
}

/// <param name="IsValid">False when the UIA result failed the sanity check (no tray element or nothing occupied).</param>
/// <param name="TrayLeft">Left edge of the tray in physical px.</param>
/// <param name="Occupied">Occupied boxes left of the tray, sorted by <see cref="Box.Left"/>.</param>
/// <param name="TrayFromTrayNotifyWnd">The UIA tray left differed by more than 300 px from TrayNotifyWnd, which was used instead.</param>
public sealed record TaskbarAnalysis(
    bool IsValid,
    string? RejectReason,
    double TrayLeft,
    IReadOnlyList<Box> Occupied,
    bool TrayFromTrayNotifyWnd,
    IReadOnlyList<(TaskbarElement Element, ElementVerdict Verdict)> Verdicts);

public static class TaskbarLayoutAnalyzer
{
    public const double TrayMismatchLimit = 300;

    /// <summary>
    /// Applies the v3 sanity check and the v3.1 occupied rule to one UIA snapshot of the taskbar.
    /// <paramref name="trayNotifyLeft"/> is the Win32 <c>TrayNotifyWnd</c> left edge (stable while Start is open), or null.
    /// </summary>
    public static TaskbarAnalysis Analyze(Box taskbar, IReadOnlyList<TaskbarElement> elements, double? trayNotifyLeft)
    {
        var anyTray = false;
        var trayLeft = taskbar.Right;
        foreach (var e in elements)
        {
            if (TaskbarElementClassifier.IsTray(e) && e.Rect.Right > e.Rect.Left && TaskbarElementClassifier.IsInside(e.Rect, taskbar))
            {
                anyTray = true;
                trayLeft = Math.Min(trayLeft, e.Rect.Left);
            }
        }

        var fromTrayNotify = false;
        if (trayNotifyLeft is { } tn && (!anyTray || Math.Abs(trayLeft - tn) > TrayMismatchLimit))
        {
            fromTrayNotify = anyTray;
            trayLeft = tn;
        }

        var verdicts = new List<(TaskbarElement, ElementVerdict)>(elements.Count);
        var occupied = new List<Box>();
        foreach (var e in elements)
        {
            var verdict = TaskbarElementClassifier.Classify(e, taskbar, trayLeft);
            verdicts.Add((e, verdict));
            if (verdict.Role == ElementRole.Occupied)
            {
                occupied.Add(e.Rect);
            }
        }
        occupied.Sort((a, b) => a.Left.CompareTo(b.Left));

        string? reject = null;
        if (!anyTray)
        {
            reject = "no tray elements";
        }
        else if (occupied.Count == 0)
        {
            reject = "no occupied elements";
        }

        return new TaskbarAnalysis(reject is null, reject, trayLeft, occupied, fromTrayNotify, verdicts);
    }
}
