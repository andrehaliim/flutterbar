using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using StatefulBar.App.Diagnostics;
using StatefulBar.App.Native;

namespace StatefulBar.App.Menu;

/// <summary>
/// Closes a context menu on a mouse press anywhere outside FlutterBar (desktop, taskbar, another app). The widget never
/// activates (WS_EX_NOACTIVATE), so its menu opens without the foreground and WPF's mouse capture does not see clicks in
/// other processes. The menu must NOT take the foreground to fix that (a ContextMenu closes itself at once when its
/// window is activated); instead a low-level mouse hook runs only while the menu is open. The press itself is passed on.
/// </summary>
internal sealed class MenuDismissal
{
    private readonly ContextMenu _menu;
    private readonly NativeMethods.LowLevelMouseProc _proc; // kept alive for as long as the hook may call it
    private IntPtr _hook;

    private MenuDismissal(ContextMenu menu)
    {
        _menu = menu;
        _proc = OnMouse;
        menu.Opened += OnOpened;
        menu.Closed += OnClosed;
    }

    public static void Attach(ContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        _ = new MenuDismissal(menu); // kept alive by the menu's event handlers
    }

    private void OnOpened(object sender, RoutedEventArgs e)
    {
        Unhook();
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            AppLog.Limited("menu-hook", $"menu mouse hook failed (error {Marshal.GetLastWin32Error()}): a click outside may not close it", 10);
        }
    }

    private void OnClosed(object sender, RoutedEventArgs e) => Unhook();

    private void Unhook()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    // Runs on the UI thread for every mouse event while the menu is open: decide fast, do the work later.
    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && IsButtonDown(wParam.ToInt64()))
        {
            var point = Marshal.PtrToStructure<NativeMethods.POINT>(lParam); // MSLLHOOKSTRUCT starts with the point
            NativeMethods.GetWindowThreadProcessId(NativeMethods.WindowFromPoint(point), out var pid);
            if (pid != (uint)Environment.ProcessId)
            {
                _menu.Dispatcher.BeginInvoke(() => _menu.IsOpen = false);
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool IsButtonDown(long message) => message is
        NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN;
}
