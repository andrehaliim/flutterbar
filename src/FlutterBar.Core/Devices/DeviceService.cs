using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Devices;

/// <summary>
/// Lists connected devices and available emulators through the project's flutter tool, and launches emulators.
/// Flutter 3.22 does not report which AVD a running emulator belongs to; pass <paramref name="adbPath"/> to resolve it
/// via <c>adb emu avd name</c>. Without adb (or if adb fails) <see cref="OfflineEmulators"/> falls back to a name match,
/// which can leave an AVD listed as offline while it runs when its device name differs (e.g. "sdk gphone64 x86 64").
/// </summary>
public sealed class DeviceService(IProcessRunner runner, FlutterToolLocator locator, string? adbPath = null)
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan AdbTimeout = TimeSpan.FromSeconds(10);

    private IReadOnlyList<DeviceInfo> _devices = [];
    private IReadOnlyList<EmulatorInfo> _emulators = [];

    public IReadOnlyList<DeviceInfo> CachedDevices => Volatile.Read(ref _devices);

    public IReadOnlyList<EmulatorInfo> CachedEmulators => Volatile.Read(ref _emulators);

    public async Task<IReadOnlyList<DeviceInfo>> RefreshDevicesAsync(string projectRoot, CancellationToken ct)
    {
        var result = await RunFlutterAsync(projectRoot, ["devices", "--machine"], ListTimeout, ct);
        var parsed = DeviceParsers.ParseDevicesMachine(result.Stdout);
        var devices = await ResolveEmulatorIdsAsync(parsed, projectRoot, ct);
        Volatile.Write(ref _devices, devices);
        return devices;
    }

    public async Task<IReadOnlyList<EmulatorInfo>> RefreshEmulatorsAsync(string projectRoot, CancellationToken ct)
    {
        var result = await RunFlutterAsync(projectRoot, ["emulators"], ListTimeout, ct);
        var emulators = DeviceParsers.ParseEmulators(result.Stdout);
        Volatile.Write(ref _emulators, emulators);
        return emulators;
    }

    /// <summary>Cached emulators with no matching connected emulator device.</summary>
    public IReadOnlyList<EmulatorInfo> OfflineEmulators() => DeviceParsers.Offline(CachedEmulators, CachedDevices);

    /// <summary>
    /// Launches <paramref name="emulatorId"/> and polls the device list every <paramref name="pollInterval"/> until the emulator
    /// shows up (a new emulator device, or one whose EmulatorId matches) or <paramref name="timeout"/> elapses (returns null).
    /// </summary>
    public async Task<DeviceInfo?> LaunchEmulatorAsync(
        string projectRoot, string emulatorId, TimeSpan pollInterval, TimeSpan timeout, CancellationToken ct)
    {
        var before = await RefreshDevicesAsync(projectRoot, ct);
        var knownIds = before.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        await RunFlutterAsync(projectRoot, ["emulators", "--launch", emulatorId], LaunchTimeout, ct);

        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            await Task.Delay(pollInterval, ct);
            var devices = await RefreshDevicesAsync(projectRoot, ct);
            var found = devices.FirstOrDefault(d =>
                d.IsEmulator
                && (!knownIds.Contains(d.Id) || string.Equals(d.EmulatorId, emulatorId, StringComparison.OrdinalIgnoreCase)));
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private async Task<ProcessResult> RunFlutterAsync(
        string projectRoot, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var resolution = locator.Resolve(projectRoot, args);
        if (resolution.Spec is null)
        {
            throw new InvalidOperationException(resolution.Error ?? "flutter can't be run");
        }

        var result = await runner.RunAsync(resolution.Spec, timeout, ct);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
            throw new InvalidOperationException($"flutter {string.Join(' ', args)} failed (exit {result.ExitCode}): {detail.Trim()}");
        }
        return result;
    }

    private async Task<IReadOnlyList<DeviceInfo>> ResolveEmulatorIdsAsync(
        IReadOnlyList<DeviceInfo> devices, string projectRoot, CancellationToken ct)
    {
        if (adbPath is null)
        {
            return devices;
        }

        var resolved = new List<DeviceInfo>(devices.Count);
        foreach (var d in devices)
        {
            if (d.IsEmulator && d.EmulatorId is null && d.Platform.StartsWith("android", StringComparison.OrdinalIgnoreCase))
            {
                var spec = new ProcessSpec(adbPath, ["-s", d.Id, "emu", "avd", "name"], projectRoot);
                var r = await runner.RunAsync(spec, AdbTimeout, ct);
                if (r.ExitCode == 0)
                {
                    var name = r.Stdout
                        .Split('\n')
                        .Select(l => l.Trim())
                        .FirstOrDefault(l => l.Length > 0 && !l.Equals("OK", StringComparison.Ordinal));
                    if (name is not null)
                    {
                        resolved.Add(d with { EmulatorId = name });
                        continue;
                    }
                }
            }
            resolved.Add(d);
        }
        return resolved;
    }
}
