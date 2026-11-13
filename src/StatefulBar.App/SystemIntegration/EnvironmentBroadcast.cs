using System.Runtime.InteropServices;

namespace FlutterBar.App.SystemIntegration;

/// <summary>Tells running programs (Explorer, new terminals started from it) that the user environment changed.</summary>
internal static class EnvironmentBroadcast
{
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint TimeoutMs = 5000;

    /// <summary>
    /// <c>WM_SETTINGCHANGE</c> "Environment" with <c>SendMessageTimeout(HWND_BROADCAST, …, SMTO_ABORTIFHUNG, 5000)</c>.
    /// Blocks up to 5 s per hung window: never call on the UI thread. Returns null on success, else the Win32 error.
    /// </summary>
    public static int? Send()
    {
        var ok = SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, TimeoutMs, out _);
        return ok == IntPtr.Zero ? Marshal.GetLastWin32Error() : null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
