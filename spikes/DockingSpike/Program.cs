using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace DockingSpike;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--probe")) return Probe();
            return new Application().Run(new MainWindow());
        }
        catch (Exception ex)
        {
            Taskbar.Log("Fatal: " + ex);
            return 1;
        }
    }

    static int Probe()
    {
        var tb = Native.FindWindow("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero) { Taskbar.Log("Shell_TrayWnd not found"); return 2; }
        Native.GetWindowRect(tb, out var r);
        Taskbar.Log($"Shell_TrayWnd rect=({r.Left},{r.Top},{r.Right},{r.Bottom}) dpi={Native.GetDpiForWindow(tb)}");
        var sb = new StringBuilder();
        Taskbar.DumpTree(AutomationElement.FromHandle(tb), 0, 6, sb);
        Taskbar.Log(sb.ToString());
        var res = Taskbar.Compute();
        if (res is not null)
            Taskbar.Log($"COMPUTED buttonsRight={res.ButtonsRight} trayLeft={res.TrayLeft} free={res.TrayLeft - res.ButtonsRight} {res.Note}");
        return 0;
    }
}
