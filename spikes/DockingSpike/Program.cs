using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace DockingSpike;

internal static class Program
{
    public static volatile bool Exiting;

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--probe")) return Probe();
            // SPIKE-ONLY test hooks (do not copy to production)
            foreach (var a in args)
                if (a.StartsWith("--force-empty-after=", StringComparison.Ordinal) && int.TryParse(a.AsSpan(20), out var n)) Taskbar.ForceEmptyAfter = n;
            foreach (var a in args)
                if (a.StartsWith("--force-min-gap=", StringComparison.Ordinal) && int.TryParse(a.AsSpan(16), out var g)) Taskbar.ForceMaxGap = g;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Exit += (_, _) => Exiting = true;
            app.DispatcherUnhandledException += (_, e) => Taskbar.Log("Unhandled: " + e.Exception);
            var win = new MainWindow();
            win.Show();
            return app.Run();
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
