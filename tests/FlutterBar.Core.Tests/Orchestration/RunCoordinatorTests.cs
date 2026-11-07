using FlutterBar.Core.Config;
using FlutterBar.Core.Devices;
using FlutterBar.Core.Diagnostics;
using FlutterBar.Core.Logging;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Settings;
using FlutterBar.Core.Tests.Fakes;

namespace FlutterBar.Core.Tests.Orchestration;

/// <summary>Fix round 1: ▶ preparation counts as an active session; a selection change cancels it.</summary>
[Collection("DiagLog")]
public sealed class RunCoordinatorTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly LaunchConfig Beta = new("beta", "lib/main.dart", "beta", RunMode.Debug, []);
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly TempProject _a = new TempProject().WithConfigs(Beta);
    private readonly TempProject _b = new TempProject().WithConfigs(Beta with { Name = "prod" });
    private readonly FakeLauncher _launcher = new();
    private readonly RunSession _session;
    private readonly GatedRunner _runner = new();
    private readonly AppSettings _settings = new();

    public RunCoordinatorTests()
    {
        DiagLog.Initialize(Path.Combine(_a.Root, "diag", "diag.log"));
        _session = new RunSession(_launcher, new LogBuffer());
        ProjectSelection.SelectProject(_settings, _a.Root);
        ProjectSelection.SelectConfig(_settings, _a.Root, "beta");
        ProjectSelection.SelectDevice(_settings, _a.Root, "emulator-5554");
    }

    public void Dispose()
    {
        _session.DisposeAsync().AsTask().Wait(Wait);
        _a.Dispose();
        _b.Dispose();
    }

    /// <summary><c>flutter devices</c> blocks until <see cref="Release"/> (or the caller cancels).</summary>
    private sealed class GatedRunner : IProcessRunner
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct)
        {
            Entered.TrySetResult();
            await _gate.Task.WaitAsync(ct);
            return new ProcessResult(0, File.ReadAllText(TestPaths.Fixture("devices", "devices-machine.json")), "");
        }
    }

    private RunCoordinator Create()
    {
        var locator = new FlutterToolLocator(new FakePaths(new() { ["flutter"] = @"C:\f\flutter.exe" }), File.Exists);
        return new RunCoordinator(_session, new RunOrchestrator(_session, new DeviceService(_runner, locator), locator));
    }

    [Fact]
    public async Task SelectionChangeDuringPreparation_CancelsRun_AndTouchesNoSettings()
    {
        var coordinator = Create();
        var run = coordinator.RunAsync(_settings, () => Now, CancellationToken.None);
        await _runner.Entered.Task.WaitAsync(Wait);
        Assert.True(coordinator.IsPreparing);

        // The user picks project B while A is still being prepared.
        Assert.True(coordinator.CancelPreparation());
        ProjectSelection.SelectProject(_settings, _b.Root);
        _runner.Release();
        var outcome = await run.WaitAsync(Wait);

        Assert.Equal(RunOutcomeKind.Cancelled, outcome.Kind);
        Assert.False(outcome.ShouldPersist);
        Assert.False(coordinator.IsPreparing);
        Assert.Empty(_launcher.Starts);
        Assert.Equal(SessionState.Idle, _session.Snapshot.State);
        Assert.True(_settings.TryGetProject(_a.Root, out var a));
        Assert.Null(a.LastUsed);
        Assert.True(_settings.TryGetProject(_b.Root, out var b));
        Assert.Null(b.Config);
        Assert.Null(b.DeviceId);
        Assert.Null(b.LastUsed);
        Assert.Equal(Path.GetFullPath(_b.Root), _settings.CurrentProject);
    }

    [Fact]
    public async Task CompletedRun_PersistsToTheProjectCapturedAtStart()
    {
        var coordinator = Create();
        _launcher.Enqueue(new FakeProcessHandle());
        var run = coordinator.RunAsync(_settings, () => Now, CancellationToken.None);
        await _runner.Entered.Task.WaitAsync(Wait);

        // Even if the current project moved without a cancel, the run's data goes to A only.
        _settings.CurrentProject = Path.GetFullPath(_b.Root);
        _runner.Release();
        var outcome = await run.WaitAsync(Wait);

        Assert.Equal(RunOutcomeKind.Started, outcome.Kind);
        Assert.Equal(Path.GetFullPath(_a.Root), outcome.ProjectRoot);
        Assert.True(_settings.TryGetProject(_a.Root, out var a));
        Assert.Equal(Now, a.LastUsed);
        Assert.Equal("beta", a.Config);
        Assert.Equal("emulator-5554", a.DeviceId);
        Assert.False(_settings.TryGetProject(_b.Root, out _));
        Assert.Equal(Path.GetFullPath(_b.Root), _settings.CurrentProject);
    }

    /// <summary>Launcher that lets the test act at the moment flutter is launched (the cancel-vs-start race).</summary>
    private sealed class HookLauncher(IProcessLauncher inner, Action onStart) : IProcessLauncher
    {
        public IProcessHandle Start(ProcessSpec spec)
        {
            onStart();
            return inner.Start(spec);
        }
    }

    [Fact]
    public async Task CancelWhileTheSessionIsLaunching_StopsIt_AndPersistsNothing()
    {
        RunCoordinator? coordinator = null;
        _launcher.Enqueue(new FakeProcessHandle());
        var session = new RunSession(new HookLauncher(_launcher, () => coordinator!.CancelPreparation()), new LogBuffer());
        var locator = new FlutterToolLocator(new FakePaths(new() { ["flutter"] = @"C:lutter.exe" }), File.Exists);
        coordinator = new RunCoordinator(session, new RunOrchestrator(session, new DeviceService(_runner, locator), locator));
        _runner.Release();

        var outcome = await coordinator.RunAsync(_settings, () => Now, CancellationToken.None);

        Assert.Equal(RunOutcomeKind.Cancelled, outcome.Kind);
        Assert.Single(_launcher.Starts);
        Assert.Equal(SessionState.Idle, session.Snapshot.State);
        Assert.True(_settings.TryGetProject(_a.Root, out var a));
        Assert.Null(a.LastUsed);
        await session.DisposeAsync();
    }

    [Fact]
    public void CancelPreparation_WhenIdle_ReturnsFalse() => Assert.False(Create().CancelPreparation());

    [Fact]
    public async Task SecondRunWhilePreparing_IsIgnored()
    {
        var coordinator = Create();
        var first = coordinator.RunAsync(_settings, () => Now, CancellationToken.None);
        await _runner.Entered.Task.WaitAsync(Wait);

        var second = await coordinator.RunAsync(_settings, () => Now, CancellationToken.None);

        Assert.Equal(RunOutcomeKind.SessionActive, second.Kind);
        coordinator.CancelPreparation();
        _runner.Release();
        Assert.Equal(RunOutcomeKind.Cancelled, (await first.WaitAsync(Wait)).Kind);
    }
}
