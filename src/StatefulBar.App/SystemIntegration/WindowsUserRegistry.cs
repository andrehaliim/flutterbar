using StatefulBar.Core.Integration;
using Microsoft.Win32;

namespace StatefulBar.App.SystemIntegration;

/// <summary>
/// The real HKCU behind <see cref="IUserRegistry"/>. Only reached from the menu items "Add CLI to PATH" and
/// "Start with Windows" (and reading the Start-with-Windows state and, on Rescan, PATH); call off the UI thread.
/// </summary>
internal sealed class WindowsUserRegistry : IUserRegistry
{
    private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>Machine PATH (HKLM, read-only, unexpanded) for re-reading PATH on Rescan; null when missing.</summary>
    public static string? ReadMachinePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(MachineEnvironmentKey, writable: false);
        return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public RegistryValue? Read(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } data)
        {
            return null;
        }
        var kind = key.GetValueKind(name);
        return new RegistryValue(data as string ?? data.ToString() ?? string.Empty, kind);
    }

    public void Write(string subKey, string name, string data, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(name, data, kind);
    }

    public void Delete(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
