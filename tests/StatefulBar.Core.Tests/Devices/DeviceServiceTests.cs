using StatefulBar.Core.Devices;
using StatefulBar.Core.Processes;

namespace StatefulBar.Core.Tests.Devices;

public class DeviceServiceTests
{
    private const string Root = @"C:\proj";
    private const string Adb = @"C:\adb\adb.exe";

    private static string Json => File.ReadAllText(TestPaths.Fixture("devices", "devices-machine.json"));

    private static string EmulatorsText => File.ReadAllText(TestPaths.Fixture("devices", "emulators.txt"));

    private sealed class FakePaths(Dictionary<string, string> map) : IPathResolver
    {
        public string? Find(string command) => map.GetValueOrDefault(command);

        public void ClearCache()
        {
        }
    }

    private sealed class FakeRunner(Func<ProcessSpec, ProcessResult> handler) : IProcessRunner
    {
        public List<(ProcessSpec Spec, TimeSpan Timeout)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add((spec, timeout));
            }
            return Task.FromResult(handler(spec));
        }
    }

    private static FlutterToolLocator Locator(bool fvm = false) =>
        new(
            new FakePaths(new() { ["fvm"] = @"C:\x\fvm.exe", ["flutter"] = @"C:\f\flutter.exe" }),
            p => fvm && p == Path.Combine(Root, ".fvmrc"));

    private static ProcessResult Ok(string stdout) => new(0, stdout, "");

    [Fact]
    public async Task Refresh_UsesProjectFlutterTool()
    {
        var runner = new FakeRunner(_ => Ok(Json));
        var svc = new DeviceService(runner, Locator(fvm: true));

        var devices = await svc.RefreshDevicesAsync(Root, CancellationToken.None);

        Assert.Equal(4, devices.Count);
        Assert.Same(devices, svc.CachedDevices);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(@"C:\x\fvm.exe", call.Spec.FileName);
        Assert.Equal(["flutter", "devices", "--machine"], call.Spec.Args);
        Assert.Equal(TimeSpan.FromSeconds(60), call.Timeout);
    }

    [Fact]
    public async Task RefreshEmulators_ParsesAndCaches()
    {
        var runner = new FakeRunner(_ => Ok(EmulatorsText));
        var svc = new DeviceService(runner, Locator());

        var list = await svc.RefreshEmulatorsAsync(Root, CancellationToken.None);

        Assert.Equal("Pixel_9", Assert.Single(list).Id);
        Assert.Equal(["emulators"], runner.Calls[0].Spec.Args);
        Assert.Same(list, svc.CachedEmulators);
    }

    [Fact]
    public async Task Refresh_ToolError_Throws_WithMessage()
    {
        var runner = new FakeRunner(_ => Ok(Json));
        var svc = new DeviceService(runner, new FlutterToolLocator(new FakePaths([]), _ => false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshDevicesAsync(Root, CancellationToken.None));

        Assert.Contains("flutter wasn't found", ex.Message);
        Assert.Empty(svc.CachedDevices);
    }

    [Fact]
    public async Task Refresh_NonZeroExit_ThrowsWithStderr_AndKeepsCache()
    {
        var fail = false;
        var runner = new FakeRunner(_ => fail ? new ProcessResult(1, "", "boom") : Ok(Json));
        var svc = new DeviceService(runner, Locator());
        await svc.RefreshDevicesAsync(Root, CancellationToken.None);
        fail = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshDevicesAsync(Root, CancellationToken.None));

        Assert.Contains("boom", ex.Message);
        Assert.Equal(4, svc.CachedDevices.Count);
    }

    [Fact]
    public async Task Refresh_WithAdb_FillsEmulatorId()
    {
        var runner = new FakeRunner(spec => spec.FileName == Adb ? Ok("Pixel_9\r\nOK\r\n") : Ok(Json));
        var svc = new DeviceService(runner, Locator(), Adb);

        var devices = await svc.RefreshDevicesAsync(Root, CancellationToken.None);

        Assert.Equal("Pixel_9", devices.Single(d => d.Id == "emulator-5554").EmulatorId);
        var adb = runner.Calls.Single(c => c.Spec.FileName == Adb);
        Assert.Equal(["-s", "emulator-5554", "emu", "avd", "name"], adb.Spec.Args);
        Assert.Equal(TimeSpan.FromSeconds(10), adb.Timeout);
    }

    [Fact]
    public async Task Refresh_WithAdb_FailureLeavesEmulatorIdNull()
    {
        var runner = new FakeRunner(spec => spec.FileName == Adb ? new ProcessResult(1, "", "offline") : Ok(Json));
        var svc = new DeviceService(runner, Locator(), Adb);

        var devices = await svc.RefreshDevicesAsync(Root, CancellationToken.None);

        Assert.Null(devices.Single(d => d.Id == "emulator-5554").EmulatorId);
    }

    [Fact]
    public async Task LaunchEmulator_PollsUntilNewEmulatorAppears()
    {
        var deviceCalls = 0;
        var runner = new FakeRunner(spec =>
        {
            if (spec.Args.Contains("--launch"))
            {
                return Ok("");
            }
            deviceCalls++;

            // call 1 = snapshot, 2 = first poll, 3 = second poll (emulator shows up)
            return Ok(deviceCalls >= 3 ? Json : "[]");
        });
        var svc = new DeviceService(runner, Locator());

        var device = await svc.LaunchEmulatorAsync(
            Root, "Pixel_9", TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("emulator-5554", device?.Id);
        Assert.Equal(3, deviceCalls);
        var launch = runner.Calls.Single(c => c.Spec.Args.Contains("--launch"));
        Assert.Equal(["emulators", "--launch", "Pixel_9"], launch.Spec.Args);
        Assert.Equal(TimeSpan.FromSeconds(120), launch.Timeout);
    }

    [Fact]
    public async Task LaunchEmulator_Timeout_ReturnsNull()
    {
        var runner = new FakeRunner(_ => Ok("[]"));
        var svc = new DeviceService(runner, Locator());

        var device = await svc.LaunchEmulatorAsync(
            Root, "Pixel_9", TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(40), CancellationToken.None);

        Assert.Null(device);
    }

    [Fact]
    public async Task LaunchEmulator_LaunchFails_Throws()
    {
        var runner = new FakeRunner(spec =>
            spec.Args.Contains("--launch") ? new ProcessResult(1, "", "no such avd") : Ok("[]"));
        var svc = new DeviceService(runner, Locator());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.LaunchEmulatorAsync(
            Root, "X", TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(40), CancellationToken.None));

        Assert.Contains("no such avd", ex.Message);
    }
}
