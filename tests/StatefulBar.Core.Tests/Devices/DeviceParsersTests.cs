using FlutterBar.Core.Devices;

namespace FlutterBar.Core.Tests.Devices;

public class DeviceParsersTests
{
    private static string Json => File.ReadAllText(TestPaths.Fixture("devices", "devices-machine.json"));

    private static string EmulatorsText => File.ReadAllText(TestPaths.Fixture("devices", "emulators.txt"));

    [Fact]
    public void ParseDevices_FromFixture()
    {
        var devices = DeviceParsers.ParseDevicesMachine(Json);

        Assert.Equal(["emulator-5554", "windows", "chrome", "edge"], devices.Select(d => d.Id));
        var chrome = devices.Single(d => d.Id == "chrome");
        Assert.Equal("web-javascript", chrome.Platform);
        Assert.False(chrome.IsEmulator);
        var emu = devices[0];
        Assert.True(emu.IsEmulator);
        Assert.Equal("android-x64", emu.Platform);
        Assert.Null(emu.EmulatorId);
    }

    [Fact]
    public void ParseDevices_ReadsEmulatorIdWhenPresent()
    {
        var devices = DeviceParsers.ParseDevicesMachine(
            """[{"name":"x","id":"emulator-5554","targetPlatform":"android-x64","emulator":true,"emulatorId":"Pixel_9"}]""");

        Assert.Equal("Pixel_9", devices.Single().EmulatorId);
    }

    [Fact]
    public void ParseDevices_SkipsBannerBeforeJson()
    {
        var devices = DeviceParsers.ParseDevicesMachine("Downloading stuff...\n" + Json);

        Assert.Equal(4, devices.Count);
    }

    [Fact]
    public void ParseEmulators_FromFixture()
    {
        var list = DeviceParsers.ParseEmulators(EmulatorsText);

        var e = Assert.Single(list);
        Assert.Equal("Pixel_9", e.Id);
        Assert.Equal("Pixel 9", e.Name);
    }

    [Fact]
    public void ParseEmulators_NoneAvailable_Empty()
    {
        var list = DeviceParsers.ParseEmulators(
            "No emulators available.\n\nYou may need to create images in Android Studio\n");

        Assert.Empty(list);
    }

    [Fact]
    public void Offline_ExcludesRunningEmulator()
    {
        var emulators = new[] { new EmulatorInfo("Pixel_9", "Pixel 9"), new EmulatorInfo("Tab_S", "Tab S") };

        var byId = DeviceParsers.Offline(
            emulators,
            [new DeviceInfo("emulator-5554", "sdk gphone", "android-x64", true, "Tab_S")]);
        var byName = DeviceParsers.Offline(
            emulators,
            [new DeviceInfo("emulator-5554", "Pixel 9", "android-x64", true, null)]);
        var physicalSameName = DeviceParsers.Offline(
            emulators,
            [new DeviceInfo("abc", "Pixel 9", "android-arm64", false, null)]);

        Assert.Equal(["Pixel_9"], byId.Select(e => e.Id));
        Assert.Equal(["Tab_S"], byName.Select(e => e.Id));
        Assert.Equal(2, physicalSameName.Count);
    }
}
