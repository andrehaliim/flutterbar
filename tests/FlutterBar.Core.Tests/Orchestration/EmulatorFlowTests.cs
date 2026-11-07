using FlutterBar.Core.Devices;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Tests.Fakes;

namespace FlutterBar.Core.Tests.Orchestration;

public class EmulatorFlowTests
{
    private const string Root = @"C:\proj";
    private const string NoDevices = "[]";

    private static string WithEmulator => File.ReadAllText(TestPaths.Fixture("devices", "devices-machine.json"));

    private static DeviceService Service(FakeRunner runner) =>
        new(runner, new FlutterToolLocator(new FakePaths(new() { ["flutter"] = @"C:\f\flutter.exe" }), _ => false));

    [Fact]
    public void Timings_MatchSpec()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), EmulatorFlow.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(120), EmulatorFlow.Timeout);
        Assert.Equal("Emulator belum siap setelah 2 menit", UiText.EmulatorTimeout);
    }

    [Fact]
    public async Task Ready_ReturnsDevice()
    {
        var polls = 0;
        var runner = new FakeRunner(spec =>
            spec.Args.Contains("--launch") ? new ProcessResult(0, "", "")
            : new ProcessResult(0, Interlocked.Increment(ref polls) > 1 ? WithEmulator : NoDevices, ""));

        var outcome = await EmulatorFlow.LaunchAsync(Service(runner), Root, "Pixel_9", TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("emulator-5554", outcome.Device?.Id);
        Assert.Null(outcome.Message);
    }

    [Fact]
    public async Task NeverReady_TimeoutMessage()
    {
        var runner = new FakeRunner(_ => new ProcessResult(0, NoDevices, ""));

        var outcome = await EmulatorFlow.LaunchAsync(Service(runner), Root, "Pixel_9", TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(30), CancellationToken.None);

        Assert.Null(outcome.Device);
        Assert.Equal(UiText.EmulatorTimeout, outcome.Message);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public async Task LaunchCommandFails_ReportsError()
    {
        var runner = new FakeRunner(spec =>
            spec.Args.Contains("--launch") ? new ProcessResult(1, "", "unknown emulator") : new ProcessResult(0, NoDevices, ""));

        var outcome = await EmulatorFlow.LaunchAsync(Service(runner), Root, "Nope", TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Null(outcome.Device);
        Assert.Contains("unknown emulator", outcome.Message);
        Assert.NotNull(outcome.Error);
    }
}
