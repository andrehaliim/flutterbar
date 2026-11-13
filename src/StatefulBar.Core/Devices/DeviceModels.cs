namespace StatefulBar.Core.Devices;

/// <summary>A device from <c>flutter devices --machine</c>. <paramref name="Platform"/> is flutter's <c>targetPlatform</c> (e.g. android-x64, web-javascript).</summary>
/// <param name="EmulatorId">AVD id this running emulator was started from; null when unknown (flutter 3.22 does not report it).</param>
public sealed record DeviceInfo(string Id, string Name, string Platform, bool IsEmulator, string? EmulatorId);

/// <summary>An available (not necessarily running) emulator from <c>flutter emulators</c>.</summary>
public sealed record EmulatorInfo(string Id, string Name);
