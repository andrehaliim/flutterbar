using Microsoft.Win32;

namespace StatefulBar.Core.Integration;

public enum StartupState
{
    /// <summary>No <c>StatefulBar</c> value in the Run key.</summary>
    Off,

    /// <summary>The Run value starts this executable.</summary>
    ThisApp,

    /// <summary>The Run value exists but points elsewhere (e.g. an older build folder).</summary>
    OtherPath,
}

/// <summary>"Start with Windows" (spec §7.4): <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\StatefulBar</c> = quoted exe path.</summary>
public static class StartupRegistration
{
    public const string SubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "StatefulBar";

    /// <summary>The value name before the rename to StatefulBar (2026-11).</summary>
    public const string LegacyValueName = "FlutterBar";

    /// <summary>
    /// One-time migration after the rename: a legacy <c>FlutterBar</c> Run value means the user had Start with Windows on,
    /// so it is deleted and, unless a <c>StatefulBar</c> value already exists, this exe is registered instead (the legacy
    /// value points at the old <c>FlutterBar.App.exe</c>). True when the legacy value was found.
    /// </summary>
    public static bool MigrateLegacy(IUserRegistry registry, string exePath)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (registry.Read(SubKey, LegacyValueName) is null)
        {
            return false;
        }
        if (registry.Read(SubKey, ValueName) is null)
        {
            Enable(registry, exePath);
        }
        registry.Delete(SubKey, LegacyValueName);
        return true;
    }

    public static string CommandFor(string exePath)
    {
        Validate(exePath);
        return "\"" + exePath + "\"";
    }

    public static StartupState Read(IUserRegistry registry, string exePath)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var value = registry.Read(SubKey, ValueName);
        if (value is null)
        {
            return StartupState.Off;
        }
        var target = value.Data.Trim().Trim('"');
        return string.Equals(target, exePath.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase) ? StartupState.ThisApp : StartupState.OtherPath;
    }

    public static void Enable(IUserRegistry registry, string exePath)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Write(SubKey, ValueName, CommandFor(exePath), RegistryValueKind.String);
    }

    public static void Disable(IUserRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Delete(SubKey, ValueName);
    }

    private static void Validate(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath) || exePath.Contains('"'))
        {
            throw new ArgumentException($"Invalid exe path for Start with Windows: \"{exePath}\"", nameof(exePath));
        }
    }
}
