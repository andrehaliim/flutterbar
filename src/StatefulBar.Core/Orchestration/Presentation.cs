using StatefulBar.Core.Config;
using StatefulBar.Core.Devices;
using StatefulBar.Core.Processes;

namespace StatefulBar.Core.Orchestration;

/// <summary>Config chip: text, red error state, tooltip (program/flavor/mode) and the error lines for the flyout.</summary>
public sealed record ConfigChipState(string Text, bool IsError, string? ToolTip, LaunchConfig? Selected, IReadOnlyList<string> ErrorLines);

public static class ConfigPresentation
{
    public static ConfigChipState Chip(LaunchConfigLoadResult result, string? selectedName)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.FileExists)
        {
            return new ConfigChipState(UiText.NoConfig, false, "launch.json doesn't exist yet", null, []);
        }
        if (result.Errors.Count > 0)
        {
            var lines = result.Errors.Select(FormatError).ToList();
            return new ConfigChipState(UiText.ConfigInvalid, true, "launch.json is invalid:\n" + string.Join('\n', lines), null, lines);
        }
        var selected = selectedName is null ? result.Configs.FirstOrDefault() : result.Configs.FirstOrDefault(c => c.Name == selectedName);
        if (selected is null)
        {
            var text = selectedName ?? UiText.PickConfig;
            return new ConfigChipState(text, true, NotFoundText(text), null, []);
        }
        return new ConfigChipState(selected.Name, false, ToolTip(selected), selected, []);
    }

    public static string NotFoundText(string name) => $"Config \"{name}\" isn't in launch.json";

    public static string ToolTip(LaunchConfig config) =>
        $"program: {config.Program}\nflavor: {config.Flavor ?? "-"}\nmode: {ModeText(config.Mode)}";

    /// <summary>One-line summary for flyout items: program · flavor · mode.</summary>
    public static string Detail(LaunchConfig config) =>
        string.Join(" · ", new[] { config.Program, config.Flavor, ModeText(config.Mode) }.Where(s => !string.IsNullOrEmpty(s)));

    public static string FormatError(ConfigError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var where = error.Line is { } line ? $"Line {line}" : error.Field;
        return where is null ? error.Message : $"{where}: {error.Message}";
    }

    private static string ModeText(RunMode mode) => mode.ToString().ToLowerInvariant();
}

public static class DevicePresentation
{
    /// <summary>Device chip: the name from the last device list, else the stored id, else "Select device".</summary>
    public static string Label(string? deviceId, IReadOnlyList<DeviceInfo> cached)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return UiText.PickDevice;
        }
        return cached.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase))?.Name ?? deviceId;
    }

    public static string Detail(DeviceInfo device) => $"{device.Platform} · {device.Id}";
}

/// <summary>
/// adb for <see cref="DeviceService"/> (AVD names of running emulators): ANDROID_HOME, ANDROID_SDK_ROOT,
/// %LOCALAPPDATA%\Android\Sdk, then PATH. Only an existing file is returned (a missing adb path would make every refresh throw).
/// </summary>
public static class AdbLocator
{
    public static string? Find(Func<string, string?> getEnv, Func<string, bool> fileExists, IPathResolver paths)
    {
        ArgumentNullException.ThrowIfNull(getEnv);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(paths);
        var candidates = new List<string?>
        {
            SdkAdb(getEnv("ANDROID_HOME")),
            SdkAdb(getEnv("ANDROID_SDK_ROOT")),
            SdkAdb(getEnv("LOCALAPPDATA") is { Length: > 0 } local ? Path.Combine(local, "Android", "Sdk") : null),
            paths.Find("adb"),
        };
        return candidates.FirstOrDefault(c => c is not null && fileExists(c));
    }

    private static string? SdkAdb(string? sdk) =>
        string.IsNullOrWhiteSpace(sdk) ? null : Path.Combine(sdk.Trim('"'), "platform-tools", "adb.exe");
}

/// <param name="Message">User-facing problem (timeout or failure); null when the emulator came up.</param>
public sealed record EmulatorOutcome(DeviceInfo? Device, string? Message, Exception? Error = null);

/// <summary>Offline emulator → launch, poll every 5 s, give up after 120 s (spec §5.4); the emulator is left running.</summary>
public static class EmulatorFlow
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static async Task<EmulatorOutcome> LaunchAsync(
        DeviceService devices, string root, string emulatorId, TimeSpan pollInterval, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        try
        {
            var device = await devices.LaunchEmulatorAsync(root, emulatorId, pollInterval, timeout, ct).ConfigureAwait(false);
            return device is null ? new EmulatorOutcome(null, UiText.EmulatorTimeout) : new EmulatorOutcome(device, null);
        }
        catch (Exception ex) when (ToolFailure.IsExpected(ex))
        {
            return new EmulatorOutcome(null, $"Couldn't start emulator {emulatorId}: {ex.Message}", ex);
        }
    }
}

/// <summary>Failures of an external tool call (flutter/adb/git) that are reported to the user instead of crashing.</summary>
public static class ToolFailure
{
    public static bool IsExpected(Exception ex) =>
        ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException;
}
