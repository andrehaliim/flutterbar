using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using FlutterBar.Core.Config;
using FlutterBar.Core.Daemon;
using FlutterBar.Core.Diagnostics;
using FlutterBar.Core.Logging;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Tests.Fakes;

namespace FlutterBar.Core.Tests.Sessions;

// Shares the DiagLog collection: error paths write to the (static) DiagLog, redirected to a temp file here.
[Collection("DiagLog")]
public sealed class RunSessionTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string StartOkAppId = "7c65a290-0448-420b-baeb-2dcde4e0017b";
    private const string AppId = "app-1";

    private readonly string _diagDir = Path.Combine(Path.GetTempPath(), "FlutterBarSession-" + Guid.NewGuid().ToString("N"));
    private readonly LogBuffer _log = new();

    public RunSessionTests()
    {
        DiagLog.Initialize(Path.Combine(_diagDir, "diag.log"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_diagDir))
            {
                Directory.Delete(_diagDir, true);
            }
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine("test cleanup failed: " + ex.Message);
        }
    }

    private static RunRequest Request(RunMode mode = RunMode.Debug) =>
        new(
            @"C:\proj",
            new LaunchConfig("beta", "lib/main.dart", null, mode, []),
            "chrome",
            new ProcessSpec("flutter", ["run", "--machine", "-d", "chrome"], @"C:\proj"));

    private static string AppStartLine(string appId = AppId, bool supportsRestart = true) =>
        $$$"""[{"event":"app.start","params":{"appId":"{{{appId}}}","deviceId":"emulator-5554","directory":"C:\\proj","supportsRestart":{{{(supportsRestart ? "true" : "false")}}},"launchMode":"run","mode":"debug"}}]""";

    private static string AppStartedLine(string appId = AppId) =>
        $$$"""[{"event":"app.started","params":{"appId":"{{{appId}}}"}}]""";

    private static string ResponseLine(int code, string message) =>
        $$$"""[{"id":0,"result":{"code":{{{code}}},"message":"{{{message}}}"}}]""";

    private static string AppLogLine(string log) =>
        JsonSerializer.Serialize(new[] { new { @event = "app.log", @params = new { appId = AppId, log } } });

    private static FakeProcessHandle RunningHandle(bool supportsRestart = true) =>
        new([AppStartLine(supportsRestart: supportsRestart), AppStartedLine()]);

    private static int RequestId(string written) =>
        JsonDocument.Parse(written).RootElement[0].GetProperty("id").GetInt32();

    private static async Task<SessionSnapshot> WaitForAsync(RunSession session, Func<SessionSnapshot, bool> predicate)
    {
        var tcs = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(SessionSnapshot s)
        {
            if (predicate(s))
            {
                tcs.TrySetResult(s);
            }
        }

        session.Changed += OnChanged;
        try
        {
            var now = session.Snapshot;
            if (predicate(now))
            {
                return now;
            }
            return await tcs.Task.WaitAsync(Wait);
        }
        finally
        {
            session.Changed -= OnChanged;
        }
    }

    private static Task<SessionSnapshot> WaitForStateAsync(RunSession session, SessionState state) =>
        WaitForAsync(session, s => s.State == state);

    private async Task<(RunSession Session, FakeProcessHandle Handle)> StartRunningAsync(
        FakeProcessHandle handle, RunMode mode = RunMode.Debug, TimeSpan? stopGrace = null, RetainedJobs? retainedJobs = null)
    {
        var session = new RunSession(new FakeLauncher(handle), _log, stopGrace, retainedJobs);
        await session.StartAsync(Request(mode));
        await WaitForStateAsync(session, SessionState.Running);
        return (session, handle);
    }

    // ---------------------------------------------------------------- start

    [Fact]
    public async Task Start_GoesStartingThenRunning_OnAppStarted()
    {
        var handle = FakeProcessHandle.FromFixture("start-ok");
        var launcher = new FakeLauncher(handle);
        await using var session = new RunSession(launcher, _log);
        var states = new ConcurrentQueue<SessionState>();
        session.Changed += s => states.Enqueue(s.State);
        var request = Request();

        Assert.Equal(SessionState.Idle, session.Snapshot.State);
        await session.StartAsync(request);
        var running = await WaitForStateAsync(session, SessionState.Running);

        Assert.Equal([SessionState.Starting, SessionState.Running], states.Distinct().Take(2));
        Assert.Same(request.Spec, Assert.Single(launcher.Starts));
        Assert.Same(request, running.Request);
        Assert.True(running.CanReload);
        Assert.True(running.CanRestart);
        Assert.False(running.HasError);
        Assert.Null(running.FailureMessage);
        Assert.Null(running.Progress);
        var separator = _log.Snapshot().First();
        Assert.Equal(LogKind.Separator, separator.Kind);
        Assert.Contains("Run beta · chrome", separator.Text);
        await handle.WhenIdle();
        Assert.Contains(_log.Snapshot(), l => l.Kind == LogKind.Tool && l.Text.StartsWith("Launching lib", StringComparison.Ordinal));
        Assert.Empty(handle.HandlerFaults);
    }

    [Fact]
    public async Task Start_ProgressMessage_ExposedWhileStarting()
    {
        var handle = new FakeProcessHandle([
            AppStartLine(),
            """[{"event":"app.progress","params":{"appId":"app-1","id":"0","progressId":null,"message":"Running Gradle task 'assembleDebug'...","finished":false}}]""",
        ]);
        await using var session = new RunSession(new FakeLauncher(handle), _log);

        await session.StartAsync(Request());
        var starting = await WaitForAsync(session, s => s.Progress is not null);

        Assert.Equal(SessionState.Starting, starting.State);
        Assert.Equal("Running Gradle task 'assembleDebug'...", starting.Progress);
        Assert.Contains(_log.Snapshot(), l => l.Kind == LogKind.Tool && l.Text == "Running Gradle task 'assembleDebug'...");

        handle.EmitStdout("""[{"event":"app.progress","params":{"appId":"app-1","id":"0","progressId":null,"finished":true}}]""");
        await WaitForAsync(session, s => s.Progress is null);
        handle.EmitStdout(AppStartedLine());
        var running = await WaitForStateAsync(session, SessionState.Running);
        Assert.Null(running.Progress);
    }

    [Fact]
    public async Task ProcessExitsBeforeStarted_Failed_LogKept()
    {
        // Review Focus #3: build-fail has app.start + app.stop but no app.started; errors only on stderr.
        var handle = FakeProcessHandle.FromFixture("build-fail");
        await using var session = new RunSession(new FakeLauncher(handle), _log);

        await session.StartAsync(Request());
        handle.Exit(1);
        var failed = await WaitForStateAsync(session, SessionState.Failed);

        Assert.NotNull(failed.FailureMessage);
        Assert.Contains("Build gagal", failed.FailureMessage);
        Assert.Contains("1", failed.FailureMessage);
        Assert.Contains("Failed to compile application.", failed.FailureMessage);
        Assert.False(failed.CanReload);
        Assert.False(failed.CanRestart);
        var lines = _log.Snapshot();
        Assert.Contains(lines, l => l.Kind == LogKind.Tool && l.Text == @"Launching lib\main.dart on Chrome in debug mode...");
        Assert.Contains(lines, l => l.Kind == LogKind.Error && l.Text.StartsWith("lib/main.dart:127:9: Error:", StringComparison.Ordinal));
        Assert.Empty(handle.HandlerFaults);
    }

    [Fact]
    public async Task OnlyNonJsonThenExit_Failed_NotStuckStarting()
    {
        // Review Focus #3: flutter prints only plain text (e.g. Gradle output) and exits.
        var handle = new FakeProcessHandle(["Running Gradle task 'assembleDebug'...", "FAILURE: Build failed with an exception."]);
        await using var session = new RunSession(new FakeLauncher(handle), _log);

        await session.StartAsync(Request());
        handle.Exit(1);
        var failed = await WaitForStateAsync(session, SessionState.Failed);

        Assert.Contains("exit code 1", failed.FailureMessage);
        Assert.Contains(_log.Snapshot(), l => l.Kind == LogKind.Tool && l.Text == "FAILURE: Build failed with an exception.");
    }

    [Fact]
    public async Task LaunchThrows_Failed_WithMessage()
    {
        var launcher = new FakeLauncher { StartException = new Win32Exception(2, "The system cannot find the file specified") };
        await using var session = new RunSession(launcher, _log);

        await session.StartAsync(Request());

        var snapshot = session.Snapshot;
        Assert.Equal(SessionState.Failed, snapshot.State);
        Assert.Contains("The system cannot find the file specified", snapshot.FailureMessage);
        Assert.Contains(_log.Snapshot(), l => l.Kind == LogKind.Error && l.Text.Contains("The system cannot find the file specified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_WhileRunning_Throws()
    {
        var (session, _) = await StartRunningAsync(FakeProcessHandle.FromFixture("start-ok"));
        await using var lifetime = session;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(Request()));
        Assert.Equal(SessionState.Running, session.Snapshot.State);
    }

    [Fact]
    public async Task StartAgain_AfterFailed_NewSessionClearsFailureAndError()
    {
        var first = new FakeProcessHandle([AppStartLine(), "══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══", "Bad state: boom"]);
        var second = RunningHandle();
        await using var session = new RunSession(new FakeLauncher(first, second), _log);
        await session.StartAsync(Request());
        first.Exit(1);
        var failed = await WaitForStateAsync(session, SessionState.Failed);
        Assert.True(failed.HasError);

        await session.StartAsync(Request());
        var running = await WaitForStateAsync(session, SessionState.Running);

        Assert.False(running.HasError);
        Assert.Equal(0, running.ErrorCount);
        Assert.Null(running.FailureMessage);
        Assert.True(first.Disposed);
        Assert.Equal(0, first.KillCount); // it had exited on its own: its job (daemons) is retained, not killed
        Assert.True(first.JobDetached);
        Assert.False(first.Job.Disposed);
    }

    // ---------------------------------------------------------------- reload

    [Fact]
    public async Task Reload_SendsEncodedRequest_ReturnsSuccess()
    {
        var (session, handle) = await StartRunningAsync(FakeProcessHandle.FromFixture("reload-ok"));
        await using var lifetime = session;

        var outcome = await session.ReloadAsync(fullRestart: false, Wait);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Null(outcome.Failure);
        var written = Assert.Single(handle.Writes);
        Assert.Equal(DaemonProtocol.EncodeRestart(RequestId(written), "d0fa5af5-6c9b-4393-a646-d5d5578efe12", false), written);
        Assert.Equal(SessionState.Running, session.Snapshot.State);
    }

    [Fact]
    public async Task Restart_SendsFullRestartTrue()
    {
        var (session, handle) = await StartRunningAsync(FakeProcessHandle.FromFixture("restart-ok"));
        await using var lifetime = session;

        var outcome = await session.ReloadAsync(fullRestart: true, Wait);

        Assert.True(outcome.Success, outcome.Message);
        var written = Assert.Single(handle.Writes);
        Assert.Equal(DaemonProtocol.EncodeRestart(RequestId(written), "4c62eb91-72d0-41da-aa1e-c5b693d4c8ae", true), written);
    }

    [Fact]
    public async Task Reload_MarksLogSeparator()
    {
        var handle = RunningHandle();
        handle.EnqueueSegment([ResponseLine(0, "Reloaded 1 of 612 libraries in 420ms.")]);
        handle.EnqueueSegment([ResponseLine(0, "")]);
        var (session, _) = await StartRunningAsync(handle);
        await using var lifetime = session;

        Assert.True((await session.ReloadAsync(false, Wait)).Success);
        var afterReload = _log.SinceLastReload();
        Assert.Contains(afterReload, l => l.Kind == LogKind.Reload && l.Text == "Reloaded 1 of 612 libraries in 420ms.");
        Assert.Contains(_log.Snapshot(), l => l.Kind == LogKind.Separator && l.Text.Contains("Hot reload", StringComparison.Ordinal));

        var restart = await session.ReloadAsync(true, Wait);
        Assert.True(restart.Success);
        Assert.Contains(_log.Snapshot(), l => l.Kind == LogKind.Separator && l.Text.Contains("Hot restart", StringComparison.Ordinal));
        Assert.Contains(_log.SinceLastReload(), l => l.Kind == LogKind.Reload && l.Text == restart.Message);
        Assert.False(string.IsNullOrWhiteSpace(restart.Message));
    }

    [Fact]
    public async Task Reload_CompileError_FailureAndErrorFlag()
    {
        var (session, handle) = await StartRunningAsync(FakeProcessHandle.FromFixture("reload-compile-error"));
        await using var lifetime = session;

        var outcome = await session.ReloadAsync(false, Wait);

        Assert.False(outcome.Success);
        Assert.Equal(ReloadFailure.Failed, outcome.Failure);
        Assert.Contains("Failed to recompile application.", outcome.Message);
        Assert.Contains("lib/main.dart:127:9: Error: Expected an identifier", outcome.Message);
        var snapshot = session.Snapshot;
        Assert.True(snapshot.HasError);
        Assert.Equal(SessionState.Running, snapshot.State);
        Assert.True(snapshot.CanReload);
        Assert.Contains(_log.SinceLastReload(), l => l.Kind == LogKind.Error && l.Text.Contains("Expected an identifier", StringComparison.Ordinal));
        Assert.Empty(handle.HandlerFaults);
    }

    [Fact]
    public async Task SuccessfulReload_ClearsErrorFlag()
    {
        var handle = RunningHandle();
        handle.EnqueueSegment(
            [ResponseLine(1, "Failed to recompile application.")],
            ["lib/main.dart:127:9: Error: Expected an identifier, but got ';'."]);
        handle.EnqueueSegment([ResponseLine(0, "Reloaded 1 of 612 libraries in 420ms.")]);
        var (session, _) = await StartRunningAsync(handle);
        await using var lifetime = session;

        Assert.Equal(ReloadFailure.Failed, (await session.ReloadAsync(false, Wait)).Failure);
        Assert.True(session.Snapshot.HasError);

        var outcome = await session.ReloadAsync(false, Wait);

        Assert.True(outcome.Success);
        Assert.Equal("Reloaded 1 of 612 libraries in 420ms.", outcome.Message);
        Assert.False(session.Snapshot.HasError);
        Assert.Equal(0, session.Snapshot.ErrorCount);
    }

    [Fact]
    public async Task Reload_WhileStarting_NoSession()
    {
        // Review Focus #4: app.start seen (appId known) but no app.started yet.
        var handle = new FakeProcessHandle([AppStartLine()]);
        await using var session = new RunSession(new FakeLauncher(handle), _log);
        await session.StartAsync(Request());
        await handle.WhenIdle();

        var outcome = await session.ReloadAsync(false, Wait).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(outcome.Success);
        Assert.Equal(ReloadFailure.NoSession, outcome.Failure);
        Assert.Empty(handle.Writes);
        Assert.Equal(SessionState.Starting, session.Snapshot.State);
    }

    [Fact]
    public async Task Reload_WhenIdle_NoSession()
    {
        await using var session = new RunSession(new FakeLauncher(), _log);

        var outcome = await session.ReloadAsync(true, Wait);

        Assert.Equal(ReloadFailure.NoSession, outcome.Failure);
    }

    [Fact]
    public async Task Reload_WhileReloading_Busy()
    {
        // Review Focus #4: the second request is answered immediately, the first one still completes.
        var (session, handle) = await StartRunningAsync(FakeProcessHandle.FromFixture("start-ok"));
        await using var lifetime = session;
        var first = session.ReloadAsync(false, Wait);
        var writes = await handle.WaitForWritesAsync(1, Wait);
        Assert.Equal(SessionState.Reloading, session.Snapshot.State);
        Assert.False(session.Snapshot.CanReload);

        var second = await session.ReloadAsync(true, Wait).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(ReloadFailure.Busy, second.Failure);
        Assert.Equal("Sedang reload", second.Message);
        Assert.Single(handle.Writes);
        handle.EmitStdout($$$"""[{"id":{{{RequestId(writes[0])}}},"result":{"code":0,"message":""}}]""");
        Assert.True((await first.WaitAsync(Wait)).Success);
        Assert.Equal(SessionState.Running, session.Snapshot.State);
    }

    [Fact]
    public async Task Reload_NoResponse_TimesOut_BackToRunning()
    {
        var (session, handle) = await StartRunningAsync(FakeProcessHandle.FromFixture("start-ok"));
        await using var lifetime = session;

        var outcome = await session.ReloadAsync(false, TimeSpan.FromMilliseconds(100)).WaitAsync(Wait);

        Assert.False(outcome.Success);
        Assert.Equal(ReloadFailure.Timeout, outcome.Failure);
        Assert.Equal(SessionState.Running, session.Snapshot.State);
        Assert.True(session.Snapshot.CanReload);

        // The pending request was dropped: the next reload is not Busy and a late answer to the old id is ignored.
        handle.EnqueueSegment([ResponseLine(0, "Reloaded in 1ms.")]);
        handle.EmitStdout($$$"""[{"id":{{{RequestId(handle.Writes[0])}}},"result":{"code":1,"message":"late"}}]""");
        var next = await session.ReloadAsync(false, Wait);
        Assert.True(next.Success, next.Message);
    }

    [Fact]
    public async Task Reload_Cancelled_Throws_BackToRunning()
    {
        var (session, _) = await StartRunningAsync(FakeProcessHandle.FromFixture("start-ok"));
        await using var lifetime = session;
        using var cts = new CancellationTokenSource();

        var reload = session.ReloadAsync(false, Wait, cts.Token);
        await WaitForStateAsync(session, SessionState.Reloading);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reload.WaitAsync(Wait));
        Assert.Equal(SessionState.Running, session.Snapshot.State);
    }

    [Fact]
    public async Task ProcessExitWhileReloading_FailedOutcomePromptly()
    {
        var (session, handle) = await StartRunningAsync(FakeProcessHandle.FromFixture("start-ok"));
        await using var lifetime = session;

        var reload = session.ReloadAsync(false, TimeSpan.FromMinutes(5));
        await handle.WaitForWritesAsync(1, Wait);
        handle.Exit(255);
        var outcome = await reload.WaitAsync(Wait);

        Assert.Equal(ReloadFailure.Failed, outcome.Failure);
        Assert.Equal(SessionState.Failed, session.Snapshot.State);
    }

    [Fact]
    public async Task ReleaseMode_CannotReloadOrRestart()
    {
        var (session, handle) = await StartRunningAsync(FakeProcessHandle.FromFixture("start-ok"), RunMode.Release);
        await using var lifetime = session;

        Assert.False(session.Snapshot.CanReload);
        Assert.False(session.Snapshot.CanRestart);
        Assert.Equal(ReloadFailure.Unsupported, (await session.ReloadAsync(false, Wait)).Failure);
        Assert.Equal(ReloadFailure.Unsupported, (await session.ReloadAsync(true, Wait)).Failure);
        Assert.Empty(handle.Writes);
    }

    [Fact]
    public async Task ProfileMode_CannotReloadOrRestart()
    {
        var (session, _) = await StartRunningAsync(FakeProcessHandle.FromFixture("start-ok"), RunMode.Profile);
        await using var lifetime = session;

        Assert.False(session.Snapshot.CanReload);
        Assert.False(session.Snapshot.CanRestart);
    }

    [Fact]
    public async Task SupportsRestartFalse_CannotRestart()
    {
        var (session, handle) = await StartRunningAsync(RunningHandle(supportsRestart: false));
        await using var lifetime = session;

        Assert.True(session.Snapshot.CanReload);
        Assert.False(session.Snapshot.CanRestart);
        Assert.Equal(ReloadFailure.Unsupported, (await session.ReloadAsync(true, Wait)).Failure);
        Assert.Empty(handle.Writes);
    }

    // Flutter 3.22.3 never rejects a reload (web turns it into a restart, see fixtures README and the identical
    // web-reload-unsupported fixture), so the rejection responses here are hand-built: an "error" response and a
    // result whose message says the operation is not supported.
    [Theory]
    [InlineData("""[{"id":0,"error":"hot reload not supported by this device"}]""", "hot reload not supported by this device")]
    [InlineData("""[{"id":0,"result":{"code":1,"message":"Hot reload is not supported on web-server devices"}}]""", "Hot reload is not supported on web-server devices")]
    public async Task ReloadRejectedByFlutter_DisablesReloadForSession(string rejection, string flutterMessage)
    {
        var handle = FakeProcessHandle.FromFixture("start-ok");
        handle.EnqueueSegment([rejection]);
        handle.EnqueueSegment([ResponseLine(0, "")]);
        var (session, _) = await StartRunningAsync(handle);
        await using var lifetime = session;

        var outcome = await session.ReloadAsync(false, Wait);

        Assert.Equal(ReloadFailure.Unsupported, outcome.Failure);
        Assert.Contains(flutterMessage, outcome.Message);
        Assert.False(session.Snapshot.CanReload);
        Assert.True(session.Snapshot.CanRestart);
        Assert.Contains(_log.SinceLastReload(), l => l.Kind == LogKind.Error && l.Text.Contains(flutterMessage, StringComparison.Ordinal));

        var again = await session.ReloadAsync(false, Wait);
        Assert.Equal(ReloadFailure.Unsupported, again.Failure);
        Assert.Contains(flutterMessage, again.Message);
        Assert.Single(handle.Writes);

        Assert.True((await session.ReloadAsync(true, Wait)).Success);
        Assert.False(session.Snapshot.CanReload);
    }

    // ---------------------------------------------------------------- classification

    [Fact]
    public async Task RuntimeException_SetsErrorFlag_ErrorLinesKindError()
    {
        // Web: the framework dump arrives as NonJson stdout right after the (successful) reload response. In the
        // recording it sits between response 1 and "Application finished." (the stop), so the reload segment is built
        // here to include it: the flag must end up set even though the success response just cleared it.
        var lines = File.ReadAllLines(TestPaths.Fixture("daemon", "3.22.3", "runtime-exception.jsonl"));
        var started = Array.FindIndex(lines, l => l.Contains("\"event\":\"app.started\"", StringComparison.Ordinal));
        var finished = Array.IndexOf(lines, "Application finished.");
        var fake = new FakeProcessHandle(lines[..(started + 1)]);
        fake.EnqueueSegment(lines[(started + 1)..finished]);
        var (session, handle) = await StartRunningAsync(fake);
        await using var lifetime = session;

        Assert.True((await session.ReloadAsync(false, Wait)).Success);
        await handle.WhenIdle();

        var snapshot = session.Snapshot;
        Assert.True(snapshot.HasError);
        Assert.Equal(1, snapshot.ErrorCount);
        var logged = _log.SinceLastReload().Where(l => l.Kind is LogKind.Tool or LogKind.Error).ToList();
        var header = logged.FindIndex(l => LogClassifier.IsExceptionHeader(l.Text));
        var footer = logged.FindIndex(l => LogClassifier.IsExceptionFooter(l.Text));
        Assert.True(header > 0 && footer > header);
        Assert.All(logged[header..(footer + 1)], l => Assert.Equal(LogKind.Error, l.Kind));
        Assert.Contains(logged, l => l.Kind == LogKind.Error && l.Text == "Bad state: boom");
        Assert.Contains(logged[..header], l => l.Kind == LogKind.Tool && l.Text.StartsWith("Restarted application in", StringComparison.Ordinal));
        Assert.DoesNotContain(logged[..header], l => l.Kind == LogKind.Error);
        Assert.Contains(_log.SinceLastReload(), l => l.Kind == LogKind.Reload);
        Assert.Empty(handle.HandlerFaults);
    }

    [Fact]
    public async Task AppLogAndDaemonLog_ClassifiedPerSpec()
    {
        // Android-style output (never seen on web): app.log / daemon.logMessage events and a device-style dump.
        var (session, handle) = await StartRunningAsync(RunningHandle());
        await using var lifetime = session;

        handle.EmitStdout(
            """[{"event":"app.log","params":{"appId":"app-1","log":"I/flutter: hello"}}]""",
            """[{"event":"daemon.logMessage","params":{"level":"status","message":"Syncing files to device..."}}]""");
        await handle.WhenIdle();
        Assert.False(session.Snapshot.HasError);

        handle.EmitStdout(
            """[{"event":"app.log","params":{"appId":"app-1","log":"════════ Exception caught by widgets library ═══════════════════════════════════"}}]""",
            """[{"event":"app.log","params":{"appId":"app-1","log":"Bad state: boom"}}]""",
            """[{"event":"app.log","params":{"appId":"app-1","log":"════════════════════════════════════════════════════════════════════════════════"}}]""",
            """[{"event":"app.log","params":{"appId":"app-1","log":"I/flutter: after"}}]""",
            """[{"event":"app.log","params":{"appId":"app-1","log":"E/flutter: boom","error":true}}]""",
            """[{"event":"daemon.logMessage","params":{"level":"error","message":"Lost connection to device."}}]""");
        await handle.WhenIdle();

        var kinds = _log.Snapshot().ToDictionary(l => l.Text, l => l.Kind);
        Assert.Equal(LogKind.App, kinds["I/flutter: hello"]);
        Assert.Equal(LogKind.Tool, kinds["Syncing files to device..."]);
        Assert.Equal(LogKind.Error, kinds["Bad state: boom"]);
        Assert.Equal(LogKind.App, kinds["I/flutter: after"]);
        Assert.Equal(LogKind.Error, kinds["E/flutter: boom"]);
        Assert.Equal(LogKind.Error, kinds["Lost connection to device."]);
        Assert.True(session.Snapshot.HasError);
        Assert.Equal(1, session.Snapshot.ErrorCount);
        Assert.Empty(handle.HandlerFaults);
    }

    [Fact]
    public async Task AndroidLogcatPrefixedException_ErrorFlagCountAndBlock()
    {
        // Android: app.log carries the logcat prefix (flutter_tools AdbLogReader format; synthesized, not recorded).
        var (session, handle) = await StartRunningAsync(RunningHandle());
        await using var lifetime = session;

        handle.EmitStdout(
            AppLogLine("I/flutter ( 4711): ══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══════════════════════════"),
            AppLogLine("I/flutter ( 4711): The following StateError was thrown building MyHomePage:"),
            AppLogLine("I/flutter ( 4711): Bad state: boom"),
            AppLogLine("I/flutter ( 4711): ════════════════════════════════════════════════════════════════════════════════"),
            AppLogLine("I/flutter ( 4711): after"));
        await handle.WhenIdle();

        var kinds = _log.Snapshot().ToDictionary(l => l.Text, l => l.Kind);
        Assert.Equal(LogKind.Error, kinds["I/flutter ( 4711): ══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══════════════════════════"]);
        Assert.Equal(LogKind.Error, kinds["I/flutter ( 4711): Bad state: boom"]);
        Assert.Equal(LogKind.Error, kinds["I/flutter ( 4711): ════════════════════════════════════════════════════════════════════════════════"]);
        Assert.Equal(LogKind.App, kinds["I/flutter ( 4711): after"]); // original text kept, block closed by the footer
        Assert.True(session.Snapshot.HasError);
        Assert.Equal(1, session.Snapshot.ErrorCount);
        Assert.Empty(handle.HandlerFaults);
    }

    [Fact]
    public async Task UnterminatedExceptionBlock_ResetOnReload()
    {
        var handle = RunningHandle();
        handle.EnqueueSegment([ResponseLine(0, "")]);
        var (session, _) = await StartRunningAsync(handle);
        await using var lifetime = session;

        // A block without its footer (output cut off) must not turn the rest of the session red.
        handle.EmitStdout("══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══", "Bad state: boom");
        handle.EmitStderr("══╡ EXCEPTION CAUGHT BY SERVICES LIBRARY ╞═══");
        await handle.WhenIdle();
        Assert.Equal(LogKind.Error, _log.Snapshot().Single(l => l.Text == "Bad state: boom").Kind);
        Assert.True((await session.ReloadAsync(false, Wait)).Success);

        handle.EmitStdout(AppLogLine("I/flutter ( 4711): after reload"), "plain tool line");
        handle.EmitStderr("Warning: deprecated");
        await handle.WhenIdle();

        var kinds = _log.SinceLastReload().ToDictionary(l => l.Text, l => l.Kind);
        Assert.Equal(LogKind.App, kinds["I/flutter ( 4711): after reload"]);
        Assert.Equal(LogKind.Tool, kinds["plain tool line"]);
        Assert.False(session.Snapshot.HasError);
        Assert.Equal(0, session.Snapshot.ErrorCount);
    }

    [Fact]
    public async Task StderrLines_KindError_DoNotCountOrFlag()
    {
        var (session, handle) = await StartRunningAsync(RunningHandle());
        await using var lifetime = session;

        handle.EmitStderr("Warning: some deprecation");
        await handle.WhenIdle();

        Assert.Contains(_log.Snapshot(), l => l.Kind == LogKind.Error && l.Text == "Warning: some deprecation");
        Assert.False(session.Snapshot.HasError);
        Assert.Equal(0, session.Snapshot.ErrorCount);
    }

    [Fact]
    public async Task ThrowingLogSubscriber_FailsSessionAndLogsDiag_ReaderNotCrashed()
    {
        var handle = RunningHandle();
        _log.Appended += line =>
        {
            if (line.Text == "explode")
            {
                throw new InvalidOperationException("subscriber boom");
            }
        };
        var (session, _) = await StartRunningAsync(handle);
        await using var lifetime = session;

        handle.EmitStdout("explode");
        var failed = await WaitForStateAsync(session, SessionState.Failed);

        Assert.Contains("subscriber boom", failed.FailureMessage);
        Assert.Empty(handle.HandlerFaults);
        Assert.Equal(1, handle.KillCount);
        Assert.Contains("subscriber boom", File.ReadAllText(Path.Combine(_diagDir, "diag.log")));
    }

    [Fact]
    public async Task ThrowingLogSubscriberOnReloadResult_ReloadStillCompletes()
    {
        var handle = RunningHandle();
        handle.EnqueueSegment([ResponseLine(0, "Reloaded in 5ms.")]);
        _log.Appended += line =>
        {
            if (line.Kind == LogKind.Reload)
            {
                throw new InvalidOperationException("subscriber boom");
            }
        };
        var (session, _) = await StartRunningAsync(handle);
        await using var lifetime = session;

        var outcome = await session.ReloadAsync(false, TimeSpan.FromMinutes(5)).WaitAsync(Wait);

        Assert.True(outcome.Success);
        await WaitForStateAsync(session, SessionState.Failed);
        Assert.Empty(handle.HandlerFaults);
    }

    [Fact]
    public async Task ThrowingChangedSubscriber_IsIsolated()
    {
        var handle = FakeProcessHandle.FromFixture("reload-ok");
        await using var session = new RunSession(new FakeLauncher(handle), _log);
        session.Changed += _ => throw new InvalidOperationException("changed boom");

        await session.StartAsync(Request());
        await WaitForStateAsync(session, SessionState.Running);
        var outcome = await session.ReloadAsync(false, Wait);

        Assert.True(outcome.Success);
        Assert.Empty(handle.HandlerFaults);
        Assert.Contains("changed boom", File.ReadAllText(Path.Combine(_diagDir, "diag.log")));
    }

    // ---------------------------------------------------------------- stop / exit

    [Fact]
    public async Task Stop_SendsAppStop_ThenIdle()
    {
        var (session, handle) = await StartRunningAsync(FakeProcessHandle.FromFixture("stop"));
        await using var lifetime = session;
        var states = new ConcurrentQueue<SessionState>();
        session.Changed += s => states.Enqueue(s.State);

        await session.StopAsync();

        var written = Assert.Single(handle.Writes);
        Assert.Equal(DaemonProtocol.EncodeStop(RequestId(written), "cfb5be47-9c9d-4fbd-9208-cbe2a9aefd26"), written);
        Assert.Equal(SessionState.Idle, session.Snapshot.State);
        Assert.Null(session.Snapshot.Request);
        Assert.Contains(SessionState.Stopping, states);
        Assert.Equal(0, handle.KillCount);
        Assert.True(handle.Disposed);
        Assert.Empty(handle.HandlerFaults);
    }

    [Fact]
    public async Task Stop_CleanExit_RetainsJob_UntilRetainedJobsDisposed()
    {
        // Spec §6.2: the job is only closed when flutter does not exit within the grace; a clean stop keeps the
        // session's daemons (Gradle, adb) warm for the next run, and they die with FlutterBar (§5.3).
        using var retained = new RetainedJobs();
        var (session, handle) = await StartRunningAsync(FakeProcessHandle.FromFixture("stop"), retainedJobs: retained);
        await using var lifetime = session;

        await session.StopAsync().WaitAsync(Wait);

        Assert.Equal(0, handle.KillCount);
        Assert.True(handle.JobDetached);
        Assert.True(handle.Disposed);
        Assert.False(handle.Job.Disposed);
        Assert.Equal(1, retained.Count);

        retained.Dispose();

        Assert.True(handle.Job.Disposed);
        Assert.Equal(0, retained.Count);
    }

    [Fact]
    public void RetainedJobs_RetainAfterDispose_DisposesAtOnce()
    {
        var retained = new RetainedJobs();
        retained.Dispose();
        var handle = new FakeProcessHandle();

        retained.Retain(handle.Job);

        Assert.True(handle.Job.Disposed);
        Assert.Equal(0, retained.Count);
    }

    [Fact]
    public async Task Stop_NoExitWithinGrace_KillsTree()
    {
        var handle = FakeProcessHandle.FromFixture("start-ok");
        handle.AutoExitOnStop = false;
        var (session, _) = await StartRunningAsync(handle, stopGrace: TimeSpan.FromMilliseconds(100));
        await using var lifetime = session;

        await session.StopAsync().WaitAsync(Wait);

        Assert.Single(handle.Writes);
        Assert.Equal(1, handle.KillCount);
        Assert.False(handle.JobDetached); // killed, not retained
        Assert.True(handle.Disposed);
        Assert.Equal(SessionState.Idle, session.Snapshot.State);
    }

    [Fact]
    public async Task Stop_WhileStarting_KillsTreeWithoutAppStop()
    {
        var handle = new FakeProcessHandle([AppStartLine()]);
        await using var session = new RunSession(new FakeLauncher(handle), _log);
        await session.StartAsync(Request());
        await handle.WhenIdle();

        await session.StopAsync().WaitAsync(Wait);

        Assert.Empty(handle.Writes);
        Assert.Equal(1, handle.KillCount);
        Assert.True(handle.Disposed);
        Assert.Equal(SessionState.Idle, session.Snapshot.State);
    }

    [Fact]
    public async Task UnexpectedExitWhileRunning_Failed()
    {
        var (session, handle) = await StartRunningAsync(FakeProcessHandle.FromFixture("start-ok"));
        await using var lifetime = session;

        handle.Exit(1);
        var failed = await WaitForStateAsync(session, SessionState.Failed);

        Assert.Contains("App berhenti", failed.FailureMessage);
        Assert.False(failed.CanReload);
        Assert.False(failed.CanRestart);
        Assert.Equal(ReloadFailure.NoSession, (await session.ReloadAsync(false, Wait)).Failure);
    }

    [Fact]
    public async Task AppStopWithError_WhileRunning_Failed()
    {
        var (session, handle) = await StartRunningAsync(RunningHandle());
        await using var lifetime = session;

        handle.EmitStdout("""[{"event":"app.stop","params":{"appId":"app-1","error":"Lost connection to device."}}]""");
        var failed = await WaitForStateAsync(session, SessionState.Failed);

        Assert.Contains("Lost connection to device.", failed.FailureMessage);
        Assert.True(failed.HasError);
    }

    [Fact]
    public async Task Dispose_WhileRunning_StopsSession()
    {
        var (session, handle) = await StartRunningAsync(FakeProcessHandle.FromFixture("stop"));

        await session.DisposeAsync();

        Assert.Single(handle.Writes);
        Assert.True(handle.Disposed);
        Assert.True(handle.Job.Disposed); // the session's own RetainedJobs ends with it (FlutterBar exit)
        Assert.Equal(SessionState.Idle, session.Snapshot.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync(Request()));
    }
}
