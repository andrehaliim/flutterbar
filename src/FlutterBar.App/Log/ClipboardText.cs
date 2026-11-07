using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FlutterBar.App.Log;

/// <summary>
/// Puts text on the clipboard with a short bounded retry (2 x 50 ms). WPF <c>Clipboard.SetText</c> retries for about
/// a second, which would stall the UI thread shared with the taskbar-owned widget while another app holds the clipboard.
/// </summary>
internal static class ClipboardText
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const int Attempts = 2;
    private const int DelayMs = 50;

    /// <exception cref="Win32Exception">The clipboard stayed locked, or the data could not be stored.</exception>
    public static void Set(string text)
    {
        var opened = false;
        for (var i = 0; i < Attempts && !(opened = OpenClipboard(IntPtr.Zero)); i++)
        {
            Thread.Sleep(DelayMs);
        }
        if (!opened)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "clipboard is locked by another application");
        }
        try
        {
            var bytes = (text.Length + 1) * 2;
            var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                GlobalFree(handle);
                throw new Win32Exception(error);
            }
            Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
            Marshal.WriteInt16(ptr, text.Length * 2, 0);
            GlobalUnlock(handle);

            EmptyClipboard();
            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                GlobalFree(handle); // ownership passes to the system only on success
                throw new Win32Exception(error);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
