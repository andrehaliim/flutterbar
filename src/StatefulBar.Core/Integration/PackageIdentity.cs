using System.Runtime.InteropServices;

namespace StatefulBar.Core.Integration;

/// <summary>
/// Whether this process runs as a packaged app (MSIX, the Microsoft Store build). A package cannot change the real
/// <c>HKCU</c> Run key or the user PATH (its registry writes go to a private hive), so the menu items that do that
/// behave differently there (<c>SystemMenu</c>): Windows starts the app through the package's startup task, and
/// <c>sbar</c> is on PATH through the package's execution alias.
/// </summary>
public static class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;

    /// <summary>Read once: the identity of a process cannot change while it runs.</summary>
    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        // Asked without a buffer: a packaged process answers ERROR_INSUFFICIENT_BUFFER, any other one
        // APPMODEL_ERROR_NO_PACKAGE.
        uint length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
