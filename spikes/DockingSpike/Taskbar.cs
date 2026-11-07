using System.IO;
using System.Text;
using System.Windows.Automation;

namespace DockingSpike;

internal static class Taskbar
{
    public static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "spike.log");

    public static void Log(string s)
    {
        Console.WriteLine(s);
        try { File.AppendAllText(LogPath, s + Environment.NewLine); }
        catch (Exception ex) { Console.WriteLine("log write failed: " + ex.Message); }
    }

    public record Result(Native.RECT Taskbar, int ButtonsRight, int TrayLeft, string Note);

    public static void DumpTree(AutomationElement el, int depth, int max, StringBuilder sb)
    {
        var c = el.Current;
        var r = c.BoundingRectangle;
        sb.AppendLine($"{new string(' ', depth * 2)}[{c.ClassName}] id='{c.AutomationId}' name='{c.Name}' type={c.ControlType.ProgrammaticName} rect=({r.Left:F0},{r.Top:F0},{r.Right:F0},{r.Bottom:F0})");
        if (depth >= max) return;
        foreach (AutomationElement ch in el.FindAll(TreeScope.Children, Condition.TrueCondition))
            DumpTree(ch, depth + 1, max, sb);
    }

    static void Walk(AutomationElement el, int depth, List<(string cls, string id, string name, System.Windows.Rect r)> acc)
    {
        var c = el.Current;
        if (c.ProcessId == Environment.ProcessId) return; // our own widget (owned by the taskbar) shows up in its UIA tree
        acc.Add((c.ClassName, c.AutomationId, c.Name, c.BoundingRectangle));
        if (depth >= 6) return;
        foreach (AutomationElement ch in el.FindAll(TreeScope.Children, Condition.TrueCondition))
            Walk(ch, depth + 1, acc);
    }

    static readonly HashSet<string> NotOccupying = new()
    {
        "Windows.UI.Input.InputSite.WindowClass", "Taskbar.TaskbarFrameAutomationPeer",
        "ReBarWindow32", "TrayNotifyWnd", "Shell_TrayWnd",
    };

    // Free area = from the right edge of everything non-tray (Start, Search, Widgets, Task View, app buttons)
    // that lies left of the tray, to the left edge of the leftmost on-screen tray element.
    public static Result? Compute() => Compute(Native.FindWindow("Shell_TrayWnd", null));

    public static Result? Compute(IntPtr tb)
    {
        if (tb == IntPtr.Zero) { Log("Shell_TrayWnd not found"); return null; }
        Native.GetWindowRect(tb, out var rect);
        var acc = new List<(string cls, string id, string name, System.Windows.Rect r)>();
        Walk(AutomationElement.FromHandle(tb), 0, acc);
        int tbWidth = rect.Right - rect.Left;
        int trayLeft = rect.Right;
        bool anyTray = false;
        foreach (var (cls, id, _, r) in acc)
        {
            if (r.IsEmpty || r.Width <= 0 || !InsideTaskbar(r, rect)) continue;
            if (TrayMatch(cls, id)) { trayLeft = Math.Min(trayLeft, (int)r.Left); anyTray = true; }
        }
        int occupiedRight = rect.Left, occupiedCount = 0;
        foreach (var (cls, id, _, r) in acc)
        {
            if (r.IsEmpty || r.Width <= 0 || !InsideTaskbar(r, rect)) continue;
            if (TrayMatch(cls, id) || NotOccupying.Contains(cls)) continue;
            if (r.Width >= tbWidth / 2.0) continue; // container
            if (r.Right > trayLeft + 1) continue;   // belongs to / overlaps tray
            if (cls == "Image") continue;
            occupiedRight = Math.Max(occupiedRight, (int)r.Right);
            occupiedCount++;
        }
        return new Result(rect, occupiedRight, trayLeft, $"occupied={occupiedCount} tray={anyTray} elements={acc.Count}");
    }

    static bool InsideTaskbar(System.Windows.Rect r, Native.RECT t) =>
        r.Left >= t.Left - 1 && r.Right <= t.Right + 1 && r.Top >= t.Top - 2 && r.Bottom <= t.Bottom + 2;

    static bool TrayMatch(string cls, string id) =>
        cls.StartsWith("SystemTray.", StringComparison.Ordinal) || id is "SystemTrayIcon" or "NotifyItemIcon";
}
