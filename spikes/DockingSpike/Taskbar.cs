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
        acc.Add((c.ClassName, c.AutomationId, c.Name, c.BoundingRectangle));
        if (depth >= 6) return;
        foreach (AutomationElement ch in el.FindAll(TreeScope.Children, Condition.TrueCondition))
            Walk(ch, depth + 1, acc);
    }

    // Free area = between rightmost app button and leftmost tray element.
    public static Result? Compute()
    {
        var tb = Native.FindWindow("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero) { Log("Shell_TrayWnd not found"); return null; }
        Native.GetWindowRect(tb, out var rect);
        var acc = new List<(string cls, string id, string name, System.Windows.Rect r)>();
        Walk(AutomationElement.FromHandle(tb), 0, acc);
        int buttonsRight = rect.Left, trayLeft = rect.Right;
        bool anyButton = false, anyTray = false;
        foreach (var (cls, id, name, r) in acc)
        {
            if (r.IsEmpty || r.Width <= 0) continue;
            if (cls == "Taskbar.TaskListButtonAutomationPeer")
            { buttonsRight = Math.Max(buttonsRight, (int)r.Right); anyButton = true; }
            else if (TrayMatch(cls, id))
            { trayLeft = Math.Min(trayLeft, (int)r.Left); anyTray = true; }
        }
        return new Result(rect, buttonsRight, trayLeft, $"buttons={anyButton} tray={anyTray} elements={acc.Count}");
    }

    static bool TrayMatch(string cls, string id) =>
        cls.StartsWith("SystemTray.", StringComparison.Ordinal) || id is "SystemTrayIcon" or "NotifyItemIcon";
}
