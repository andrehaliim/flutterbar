using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DockingSpike;

public partial class MainWindow : Window
{
    IntPtr _hwnd, _hook;
    Native.WinEventDelegate? _cb; // keep alive
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
            _cb = OnForeground;
            _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _cb, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            Reposition();
        };
        _timer.Tick += (_, _) => Reposition();
        _timer.Start();
        Closed += (_, _) => { if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook); };
    }

    void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var tb = Native.FindWindow("Shell_TrayWnd", null);
        if (hwnd == tb && tb != IntPtr.Zero)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE);
            Taskbar.Log($"{DateTime.Now:HH:mm:ss.fff} foreground=Shell_TrayWnd -> reapplied topmost");
        }
    }

    void Reposition()
    {
        try
        {
            var res = Taskbar.Compute();
            if (res is null) { Info.Text = "taskbar not found"; return; }
            uint dpi = Native.GetDpiForWindow(_hwnd);
            double scale = dpi / 96.0;
            int w = (int)(300 * scale), h = (int)(40 * scale);
            int free = res.TrayLeft - res.ButtonsRight;
            int x = res.TrayLeft - w - (int)(4 * scale);
            int y = res.Taskbar.Top + (res.Taskbar.Bottom - res.Taskbar.Top - h) / 2;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
            Native.GetWindowRect(_hwnd, out var wr);
            Info.Text = $"free={free}px DPI={dpi} ({scale * 100:F0}%)\nwin=({wr.Left},{wr.Top},{wr.Right},{wr.Bottom})";
            Taskbar.Log($"{DateTime.Now:HH:mm:ss.fff} taskbar=({res.Taskbar.Left},{res.Taskbar.Top},{res.Taskbar.Right},{res.Taskbar.Bottom}) buttonsRight={res.ButtonsRight} trayLeft={res.TrayLeft} free={free} dpi={dpi} win=({wr.Left},{wr.Top},{wr.Right},{wr.Bottom}) {res.Note}");
        }
        catch (Exception ex)
        {
            Taskbar.Log("Reposition failed: " + ex);
            Info.Text = "error: " + ex.Message;
        }
    }
}
