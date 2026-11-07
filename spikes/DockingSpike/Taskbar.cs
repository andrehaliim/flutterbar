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

    // Test hook (--force-empty-after=N): after N good queries, pretend UIA returned nothing (simulates Start-open glitch).
    public static int ForceEmptyAfter = -1;
    static int _goodCount;
    static readonly Dictionary<string, DateTime> _lastLog = new();

    public static void LogLimited(string key, string msg)
    {
        if (_lastLog.TryGetValue(key, out var t) && (DateTime.Now - t).TotalSeconds < 5) return;
        _lastLog[key] = DateTime.Now;
        Log(msg);
    }

    public record Result(Native.RECT Taskbar, int ButtonsRight, int TrayLeft, string Note, List<(int L, int R)> Boxes);

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
        if (ForceEmptyAfter >= 0 && _goodCount >= ForceEmptyAfter) acc.Clear();
        int tbWidth = rect.Right - rect.Left;
        int trayLeft = rect.Right;
        bool anyTray = false;
        foreach (var (cls, id, _, r) in acc)
        {
            if (r.IsEmpty || r.Width <= 0 || !InsideTaskbar(r, rect)) continue;
            if (TrayMatch(cls, id)) { trayLeft = Math.Min(trayLeft, (int)r.Left); anyTray = true; }
        }
        // Win32 TrayNotifyWnd: stable even while Start is open; used to cross-check the UIA tray-left.
        int tnLeft = -1;
        var tn = Native.FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tn != IntPtr.Zero && Native.GetWindowRect(tn, out var tnr) && tnr.Right > tnr.Left && tnr.Left > rect.Left) tnLeft = tnr.Left;
        if (anyTray && tnLeft >= 0 && Math.Abs(trayLeft - tnLeft) > 300)
        {
            LogLimited("tray-mismatch", $"{DateTime.Now:HH:mm:ss.fff} tray-left mismatch: UIA={trayLeft} TrayNotifyWnd={tnLeft} -> using TrayNotifyWnd");
            trayLeft = tnLeft;
        }
        int occupiedRight = rect.Left, occupiedCount = 0;
        var boxes = new List<(int L, int R)>();
        foreach (var (cls, id, _, r) in acc)
        {
            if (r.IsEmpty || r.Width <= 0 || !InsideTaskbar(r, rect)) continue;
            if (TrayMatch(cls, id) || NotOccupying.Contains(cls)) continue;
            if (r.Width >= tbWidth / 2.0) continue; // container
            if (r.Right > trayLeft + 1) continue;   // belongs to / overlaps tray
            if (cls == "Image") continue;
            occupiedRight = Math.Max(occupiedRight, (int)r.Right);
            occupiedCount++;
            boxes.Add(((int)r.Left, (int)r.Right));
        }
        boxes.Sort();
        if (!anyTray || occupiedCount == 0)
        {
            LogLimited("layout-invalid", $"{DateTime.Now:HH:mm:ss.fff} UIA layout REJECTED (tray elements={anyTray}, occupied={occupiedCount}, elements={acc.Count}, TrayNotifyWnd.left={tnLeft}) -> keeping last good layout");
            return null;
        }
        _goodCount++;
        return new Result(rect, occupiedRight, trayLeft, $"occupied={occupiedCount} tray={anyTray} tnLeft={tnLeft} elements={acc.Count}", boxes);
    }

    // Gaps between occupied boxes (merged) and the tray, each inset by `inset` px, left to right.
    public static List<(int L, int R)> Gaps(Result res, int inset)
    {
        var gaps = new List<(int L, int R)>();
        int cursor = res.Taskbar.Left;
        foreach (var (l, r) in res.Boxes)
        {
            if (l - inset > cursor + inset) gaps.Add((cursor + inset, l - inset));
            cursor = Math.Max(cursor, r);
        }
        if (res.TrayLeft - inset > cursor + inset) gaps.Add((cursor + inset, res.TrayLeft - inset));
        return gaps;
    }

    static bool InsideTaskbar(System.Windows.Rect r, Native.RECT t) =>
        r.Left >= t.Left - 1 && r.Right <= t.Right + 1 && r.Top >= t.Top - 2 && r.Bottom <= t.Bottom + 2;

    static bool TrayMatch(string cls, string id) =>
        cls.StartsWith("SystemTray.", StringComparison.Ordinal) || id is "SystemTrayIcon" or "NotifyItemIcon";
}
