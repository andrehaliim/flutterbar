using FlutterBar.Core.Config;
using FlutterBar.Core.Devices;
using FlutterBar.Core.Diagnostics;
using FlutterBar.Core.Logging;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Tests.Fakes;

namespace FlutterBar.Core.Tests.Orchestration;

/// <summary>Temp project folder with lib/main.dart (launch.json validation checks that the program exists).</summary>
internal sealed class TempProject : IDisposable
{
    public TempProject()
    {
        Root = Directory.CreateTempSubdirectory("flutterbar-orch-").FullName;
        Directory.CreateDirectory(Path.Combine(Root, "lib"));
        File.WriteAllText(Path.Combine(Root, "lib", "main.dart"), "void main() {}\n");
    }

    public string Root { get; }

    public TempProject WithConfigs(params LaunchConfig[] configs)
    {
        LaunchConfigStore.Save(Root, configs);
        return this;
    }

    public TempProject WithRawLaunchJson(string text)
    {
        Directory.CreateDirectory(Path.Combine(Root, ".flutterbar"));
        File.WriteAllText(LaunchConfigStore.PathFor(Root), text);
        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}

[Collection("DiagLog")]
public sealed class RunOrchestratorTests : IDisposable
{
    private const string Flutter = @"C:\f\flutter.exe";
    private static readonly LaunchConfig Beta = new("beta", "lib/main.dart", "beta", RunMode.Debug, ["--dart-define=A=1"]);
    private static readonly LaunchConfig Prod = new("prod", "lib/main.dart", null, RunMode.Release, []);

    private readonly TempProject _project = new();
    private readonly FakeLauncher _launcher = new();
    private readonly RunSession _session;
    private bool _devicesFail;

    public RunOrchestratorTests()
    {
        DiagLog.Initialize(Path.Combine(_project.Root, "diag", "diag.log")); // RunSession logs launch failures
        _session = new RunSession(_launcher, new LogBuffer());
        Runner = new FakeRunner(spec => _devicesFail
            ? new ProcessResult(1, "", "adb server crashed")
            : new ProcessResult(0, File.ReadAllText(TestPaths.Fixture("devices", "devices-machine.json")), ""));
    }

    private FakeRunner Runner { get; }

    public void Dispose()
    {
        _session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        _project.Dispose();
    }

    private RunOrchestrator Create(Dictionary<string, string>? paths = null)
    {
        var locator = new FlutterToolLocator(new FakePaths(paths ?? new() { ["flutter"] = Flutter }), File.Exists);
        return new RunOrchestrator(_session, new DeviceService(Runner, locator), locator);
    }

    private RunSelection Selection(string? config = "beta", string? device = "emulator-5554") => new(_project.Root, config, device);

    [Fact]
    public async Task NoProject_Aborts()
    {
        var outcome = await Create().RunAsync(new RunSelection(null, "beta", "chrome"), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.NoProject, outcome.Kind);
        Assert.Empty(Runner.Calls);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public async Task ProjectFolderGone_Aborts()
    {
        var outcome = await Create().RunAsync(new RunSelection(Path.Combine(_project.Root, "gone"), "beta", "chrome"), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.ProjectMissing, outcome.Kind);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public async Task NoLaunchJson_ReportsNoConfig_WithoutTouchingDevices()
    {
        var outcome = await Create().RunAsync(Selection(), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.NoConfig, outcome.Kind);
        Assert.Equal(UiText.NoConfig, outcome.Message);
        Assert.Empty(Runner.Calls);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public async Task InvalidLaunchJson_ReportsConfigInvalid()
    {
        _project.WithRawLaunchJson("{ \"version\": 2, \"configurations\": [] }");

        var outcome = await Create().RunAsync(Selection(), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.ConfigInvalid, outcome.Kind);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public async Task UnknownConfigName_ReportsConfigNotFound()
    {
        _project.WithConfigs(Beta);

        var outcome = await Create().RunAsync(Selection(config: "staging"), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.ConfigNotFound, outcome.Kind);
        Assert.Contains("staging", outcome.Message);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public async Task NoDeviceSelected_ReportsNoDevice_WithoutRefreshing()
    {
        _project.WithConfigs(Beta);

        var outcome = await Create().RunAsync(Selection(device: null), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.NoDevice, outcome.Kind);
        Assert.Empty(Runner.Calls);
    }

    [Fact]
    public async Task DeviceNotConnected_RefreshesThenAborts()
    {
        _project.WithConfigs(Beta);

        var outcome = await Create().RunAsync(Selection(device: "R58M123"), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.DeviceMissing, outcome.Kind);
        Assert.Contains("R58M123", outcome.Message);
        Assert.Equal(["devices", "--machine"], Assert.Single(Runner.Calls).Args);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public async Task DeviceRefreshFails_ReportsError()
    {
        _project.WithConfigs(Beta);
        _devicesFail = true;

        var outcome = await Create().RunAsync(Selection(), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.DeviceRefreshFailed, outcome.Kind);
        Assert.Contains("adb server crashed", outcome.Message);
        Assert.NotNull(outcome.Error);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public async Task FvmProjectWithoutFvm_ToolError_BeforeAnyProcess()
    {
        _project.WithConfigs(Beta);
        File.WriteAllText(Path.Combine(_project.Root, ".fvmrc"), "{\"flutter\":\"3.22.0\"}");

        var outcome = await Create().RunAsync(Selection(), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.ToolError, outcome.Kind);
        Assert.Contains("fvm", outcome.Message);
        Assert.Empty(Runner.Calls);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public async Task ArgWithQuote_ToolError()
    {
        _project.WithConfigs(Beta with { Args = ["--dart-define=NAME=\"x\""] });

        var outcome = await Create().RunAsync(Selection(), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.ToolError, outcome.Kind);
        Assert.Contains("kutip", outcome.Message);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public async Task Success_StartsSessionWithBuiltArgs()
    {
        _project.WithConfigs(Beta, Prod);
        _launcher.Enqueue(new FakeProcessHandle());

        var outcome = await Create().RunAsync(Selection(), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.Started, outcome.Kind);
        Assert.True(outcome.ShouldPersist);
        Assert.Equal("beta", outcome.Config?.Name);
        Assert.Equal("emulator-5554", outcome.DeviceId);
        var spec = Assert.Single(_launcher.Starts);
        Assert.Equal(Flutter, spec.FileName);
        Assert.Equal(_project.Root, spec.WorkingDirectory);
        Assert.Equal(
            ["run", "--machine", "-d", "emulator-5554", "-t", "lib/main.dart", "--flavor", "beta", "--debug", "--dart-define=A=1"],
            spec.Args);
        Assert.Equal(SessionState.Starting, _session.Snapshot.State);
        Assert.Equal("beta", _session.Snapshot.Request?.Config.Name);
    }

    [Fact]
    public async Task NoConfigName_UsesFirstConfig()
    {
        _project.WithConfigs(Prod, Beta);
        _launcher.Enqueue(new FakeProcessHandle());

        var outcome = await Create().RunAsync(Selection(config: null), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.Started, outcome.Kind);
        Assert.Equal("prod", outcome.Config?.Name);
        Assert.Contains("--release", Assert.Single(_launcher.Starts).Args);
    }

    [Fact]
    public async Task ActiveSession_IsNotReplaced()
    {
        _project.WithConfigs(Beta);
        _launcher.Enqueue(new FakeProcessHandle());
        var orchestrator = Create();
        await orchestrator.RunAsync(Selection(), CancellationToken.None);

        var outcome = await orchestrator.RunAsync(Selection(), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.SessionActive, outcome.Kind);
        Assert.Single(_launcher.Starts);
    }

    [Fact]
    public async Task LaunchFailure_ReportsStartFailed_ButStillPersists()
    {
        _project.WithConfigs(Beta);
        _launcher.StartException = new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified");

        var outcome = await Create().RunAsync(Selection(), CancellationToken.None);

        Assert.Equal(RunOutcomeKind.StartFailed, outcome.Kind);
        Assert.True(outcome.ShouldPersist);
        Assert.Contains("cannot find the file", outcome.Message);
        Assert.Equal(SessionState.Failed, _session.Snapshot.State);
    }

    [Fact]
    public async Task Cancelled_Throws()
    {
        _project.WithConfigs(Beta);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().RunAsync(Selection(), cts.Token));
        Assert.Empty(_launcher.Starts);
    }
}
