using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using StatefulBar.App.Diagnostics;
using StatefulBar.App.Native;

namespace StatefulBar.App.Docking;

/// <summary>
/// Process name (file name without extension) per PID, cached. Uses <c>QueryFullProcessImageName</c> with
/// PROCESS_QUERY_LIMITED_INFORMATION instead of <c>Process.GetProcessById(...).ProcessName</c>, which snapshots every
/// process on the machine per call. Entries expire after two minutes (PID reuse). Thread-safe.
/// </summary>
internal static class ProcessNames
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<uint, (string? Name, DateTime At)> Cache = new();

    /// <summary>Null when the lookup failed (logged once per PID while cached).</summary>
    public static string? Of(uint pid)
    {
        var now = DateTime.UtcNow;
        if (Cache.TryGetValue(pid, out var hit) && now - hit.At < Lifetime)
        {
            return hit.Name;
        }
        if (Cache.Count > 512)
        {
            Cache.Clear();
        }
        var name = Query(pid);
        Cache[pid] = (name, now);
        return name;
    }

    public static string? OfWindow(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : Of(pid);
    }

    private static string? Query(uint pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            AppLog.Write($"process name lookup failed for pid {pid}: OpenProcess error {Marshal.GetLastWin32Error()}");
            return null;
        }
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                AppLog.Write($"process name lookup failed for pid {pid}: QueryFullProcessImageName error {Marshal.GetLastWin32Error()}");
                return null;
            }
            return Path.GetFileNameWithoutExtension(buffer.ToString());
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
