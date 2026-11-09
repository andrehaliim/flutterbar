using System.ComponentModel;
using FlutterBar.Core.Logging;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Tests.Fakes;

namespace FlutterBar.Core.Tests.Orchestration;

public class FlutterCommandsTests
{
    private const string Root = @"C:\proj";

    private sealed class FakePaths(Dictionary<string, string> map) : IPathResolver
    {
        public string? Find(string command) => map.GetValueOrDefault(command);

        public void ClearCache()
        {
        }
    }

    private static FlutterToolLocator Locator() =>
        new(new FakePaths(new() { ["flutter"] = @"C:\f\flutter.exe", ["dart"] = @"C:\f\dart.exe" }), _ => false);

    [Theory]
    [InlineData(FlutterCommand.PubGet, "flutter pub get")]
    [InlineData(FlutterCommand.Clean, "flutter clean")]
    [InlineData(FlutterCommand.CleanAndPubGet, "flutter clean + pub get")]
    [InlineData(FlutterCommand.BuildRunner, "dart run build_runner build --delete-conflicting-outputs")]
    [InlineData(FlutterCommand.Doctor, "flutter doctor")]
    public void Display_NamesWhatRuns(FlutterCommand command, string display) =>
        Assert.Equal(display, FlutterCommands.Display(command));

    [Fact]
    public void CleanAndPubGet_IsTwoSteps() =>
        Assert.Equal(["flutter clean", "flutter pub get"], FlutterCommands.Steps(FlutterCommand.CleanAndPubGet).Select(s => s.Display));

    [Theory]
    [InlineData(SessionState.Idle)]
    [InlineData(SessionState.Failed)]
    public void NoSession_EverythingRuns(SessionState state)
    {
        foreach (var command in Enum.GetValues<FlutterCommand>())
        {
            Assert.Null(FlutterCommands.Blocked(command, state));
        }
    }

    [Theory]
    [InlineData(SessionState.Starting)]
    [InlineData(SessionState.Running)]
    [InlineData(SessionState.Reloading)]
    [InlineData(SessionState.Stopping)]
    public void ActiveSession_BlocksClean(SessionState state)
    {
        Assert.Equal(FlutterCommands.StopAppFirst, FlutterCommands.Blocked(FlutterCommand.Clean, state));
        Assert.Equal(FlutterCommands.StopAppFirst, FlutterCommands.Blocked(FlutterCommand.CleanAndPubGet, state));
        Assert.Null(FlutterCommands.Blocked(FlutterCommand.Doctor, state));
    }

    [Fact]
    public void PubGet_RunsNextToARunningApp_NotWhileStartingOrStopping()
    {
        Assert.Null(FlutterCommands.Blocked(FlutterCommand.PubGet, SessionState.Running));
        Assert.Null(FlutterCommands.Blocked(FlutterCommand.BuildRunner, SessionState.Reloading));
        Assert.Equal(FlutterCommands.WaitForStart, FlutterCommands.Blocked(FlutterCommand.PubGet, SessionState.Starting));
        Assert.Equal(FlutterCommands.WaitForStop, FlutterCommands.Blocked(FlutterCommand.BuildRunner, SessionState.Stopping));
    }

    /// <summary>
    /// Emits the output and the exit once the runner started the process: the fake schedules its startup output when it
    /// starts, so an Exit scheduled before that would complete before the output (a real process drains first).
    /// </summary>
    private static async Task OutputThenExitWhenStarted(FakeProcessHandle handle, string[] stdout, string[] stderr, int exitCode)
    {
        while (handle.Spec is null)
        {
            await Task.Delay(5);
        }
        handle.EmitStdout(stdout);
        handle.EmitStderr(stderr);
        handle.Exit(exitCode);
    }

    [Fact]
    public async Task Run_StreamsOutputAfterASeparator()
    {
        var handle = new FakeProcessHandle();
        var launcher = new FakeLauncher(handle);
        var log = new LogBuffer();

        var run = new FlutterCommandRunner(launcher, Locator(), log).RunAsync(Root, FlutterCommand.PubGet, CancellationToken.None);
        await OutputThenExitWhenStarted(handle, ["Resolving dependencies...", "Got dependencies!"], ["note: something"], 0);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(FlutterCommandOutcome.Succeeded, result.Outcome);
        Assert.Equal(["pub", "get"], launcher.Starts.Single().Args);
        Assert.Equal(Root, launcher.Starts.Single().WorkingDirectory);
        Assert.True(handle.StdinClosed);
        var lines = log.Snapshot();
        Assert.Equal(result.FirstSeq, lines[0].Seq);
        Assert.Equal(LogKind.Separator, lines[0].Kind);
        Assert.Contains("flutter pub get", lines[0].Text);
        Assert.Contains(lines, l => l.Kind == LogKind.Tool && l.Text == "Got dependencies!");
        Assert.Contains(lines, l => l.Kind == LogKind.Error && l.Text == "note: something");
        Assert.Equal("flutter pub get finished", lines[^1].Text);
        Assert.Empty(handle.HandlerFaults);
    }

    [Fact]
    public async Task Run_SeparatorDoesNotMoveTheReloadMarker()
    {
        var log = new LogBuffer();
        log.MarkSessionStart("Session");
        log.Append(LogKind.App, "app line");
        var handle = new FakeProcessHandle();

        var run = new FlutterCommandRunner(new FakeLauncher(handle), Locator(), log).RunAsync(Root, FlutterCommand.Doctor, CancellationToken.None);
        await OutputThenExitWhenStarted(handle, ["done"], [], 0);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("app line", log.SinceLastReload()[0].Text);
        Assert.Contains(log.Snapshot(), l => l.Text == "done");
    }

    [Fact]
    public async Task Run_FailedStep_StopsTheCommand()
    {
        var clean = new FakeProcessHandle();
        clean.Exit(1);
        var launcher = new FakeLauncher(clean);
        var log = new LogBuffer();

        var result = await new FlutterCommandRunner(launcher, Locator(), log).RunAsync(Root, FlutterCommand.CleanAndPubGet, CancellationToken.None);

        Assert.Equal(FlutterCommandOutcome.Failed, result.Outcome);
        Assert.Equal("flutter clean failed (exit code 1)", result.Message);
        Assert.Single(launcher.Starts); // pub get never started
        Assert.Equal(LogKind.Error, log.Snapshot()[^1].Kind);
        Assert.True(result.FirstSeq > 0);
    }

    [Fact]
    public async Task Run_BothStepsOfCleanAndPubGet()
    {
        var clean = new FakeProcessHandle();
        clean.Exit(0);
        var pubGet = new FakeProcessHandle();
        pubGet.Exit(0);
        var launcher = new FakeLauncher(clean, pubGet);

        var result = await new FlutterCommandRunner(launcher, Locator(), new LogBuffer()).RunAsync(Root, FlutterCommand.CleanAndPubGet, CancellationToken.None);

        Assert.Equal(FlutterCommandOutcome.Succeeded, result.Outcome);
        Assert.Equal([["clean"], ["pub", "get"]], launcher.Starts.Select(s => s.Args.ToArray()));
    }

    [Fact]
    public async Task Run_BuildRunner_UsesDart()
    {
        var handle = new FakeProcessHandle();
        handle.Exit(0);
        var launcher = new FakeLauncher(handle);

        await new FlutterCommandRunner(launcher, Locator(), new LogBuffer()).RunAsync(Root, FlutterCommand.BuildRunner, CancellationToken.None);

        Assert.Equal(@"C:\f\dart.exe", launcher.Starts.Single().FileName);
        Assert.Equal(["run", "build_runner", "build", "--delete-conflicting-outputs"], launcher.Starts.Single().Args);
    }

    [Fact]
    public async Task Run_Cancelled_KillsTheProcess()
    {
        var handle = new FakeProcessHandle(); // never exits on its own
        using var cts = new CancellationTokenSource();
        var log = new LogBuffer();
        var run = new FlutterCommandRunner(new FakeLauncher(handle), Locator(), log).RunAsync(Root, FlutterCommand.Doctor, cts.Token);

        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(FlutterCommandOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, handle.KillCount);
        Assert.True(handle.Disposed);
        Assert.Equal("flutter doctor cancelled", log.Snapshot()[^1].Text);
    }

    [Fact]
    public async Task Run_ToolMissing_FailsWithoutStarting()
    {
        var launcher = new FakeLauncher();
        var locator = new FlutterToolLocator(new FakePaths([]), _ => false);

        var result = await new FlutterCommandRunner(launcher, locator, new LogBuffer()).RunAsync(Root, FlutterCommand.PubGet, CancellationToken.None);

        Assert.Equal(FlutterCommandOutcome.Failed, result.Outcome);
        Assert.Equal("flutter wasn't found on PATH", result.Message);
        Assert.Empty(launcher.Starts);
    }

    [Fact]
    public async Task Run_StartFails_ReportsIt()
    {
        var launcher = new FakeLauncher { StartException = new Win32Exception(2, "The system cannot find the file specified") };

        var result = await new FlutterCommandRunner(launcher, Locator(), new LogBuffer()).RunAsync(Root, FlutterCommand.Clean, CancellationToken.None);

        Assert.Equal(FlutterCommandOutcome.Failed, result.Outcome);
        Assert.StartsWith("flutter clean couldn't start:", result.Message);
    }

    [Fact]
    public void Terminal_PrefersWindowsTerminal()
    {
        var choice = TerminalLauncher.Resolve(new FakePaths(new() { ["wt"] = @"C:\apps\wt.exe", ["powershell"] = @"C:\ps\powershell.exe" }), Root);

        Assert.Equal(TerminalLauncher.WindowsTerminal, choice.Name);
        Assert.Equal(@"C:\apps\wt.exe", choice.Spec.FileName);
        Assert.Equal(["-d", Root], choice.Spec.Args);
        Assert.Equal(Root, choice.Spec.WorkingDirectory);
    }

    [Fact]
    public void Terminal_FallsBackToPowerShell()
    {
        var choice = TerminalLauncher.Resolve(new FakePaths([]), Root);

        Assert.Equal(TerminalLauncher.PowerShell, choice.Name);
        Assert.Equal("powershell.exe", choice.Spec.FileName);
        Assert.Equal(["-NoExit"], choice.Spec.Args);
        Assert.Equal(Root, choice.Spec.WorkingDirectory);
    }
}
