using FlutterBar.Core.Integration;
using Microsoft.Win32;

namespace FlutterBar.App.SystemIntegration;

/// <summary>
/// The real HKCU behind <see cref="IUserRegistry"/>. Only reached from the menu items "Tambahkan CLI ke PATH" and
/// "Start with Windows" (and reading the Start-with-Windows state); call off the UI thread.
/// </summary>
internal sealed class WindowsUserRegistry : IUserRegistry
{
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
