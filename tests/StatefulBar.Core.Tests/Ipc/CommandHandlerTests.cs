using System.Text.Json;
using StatefulBar.Core.Config;
using StatefulBar.Core.Diagnostics;
using StatefulBar.Core.Ipc;
using StatefulBar.Core.Logging;
using StatefulBar.Core.Processes;
using StatefulBar.Core.Sessions;
using StatefulBar.Core.Tests.Fakes;

namespace StatefulBar.Core.Tests.Ipc;

[Collection("DiagLog")]
public sealed class CommandHandlerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string AppId = "app-1";

    private readonly string _diagDir = Path.Combine(Path.GetTempPath(), "StatefulBarIpc-" + Guid.NewGuid().ToString("N"));
    private readonly LogBuffer _log = new();
    private int _shown;

    public CommandHandlerTests()
    {
        DiagLog.Initialize(Path.Combine(_diagDir, "diag.log"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_diagDir))
        {
            Directory.Delete(_diagDir, true);
        }
    }

    private sealed class FakeStatus : IStatusProvider
    {
        public StatusInfo Current() => new("Running", "my-app", "main", "beta", "emulator-5554", 0);
    }

    private static RunRequest Request() =>
        new(
            @"C:\proj",
            new LaunchConfig("beta", "lib/main.dart", null, RunMode.Debug, []),
            "chrome",
            new ProcessSpec("flutter", ["run", "--machine", "-d", "chrome"], @"C:\proj"));

    private static string AppStartLine() =>
        $$$"""[{"event":"app.start","params":{"appId":"{{{AppId}}}","deviceId":"emulator-5554","directory":"C:\\proj","supportsRestart":true,"launchMode":"run","mode":"debug"}}]""";

    private static string AppStartedLine() => $$$"""[{"event":"app.started","params":{"appId":"{{{AppId}}}"}}]""";

    private static async Task WaitStateAsync(RunSession session, SessionState state)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(SessionSnapshot s)
        {
            if (s.State == state)
            {
                tcs.TrySetResult();
            }
        }

        session.Changed += OnChanged;
        try
        {
            if (session.Snapshot.State != state)
            {
                await tcs.Task.WaitAsync(Wait);
            }
        }
        finally
        {
            session.Changed -= OnChanged;
        }
    }

    private async Task<(RunSession Session, FakeProcessHandle Handle)> RunningAsync(FakeProcessHandle handle, TimeSpan? stopGrace = null)
    {
        var session = new RunSession(new FakeLauncher(handle), _log, stopGrace);
        await session.StartAsync(Request());
        await WaitStateAsync(session, SessionState.Running);
        return (session, handle);
    }

    private CommandHandler Handler(RunSession session, TimeSpan? reloadTimeout = null) =>
        new(session, _log, new FakeStatus(), () => Interlocked.Increment(ref _shown), reloadTimeout);

    private static IpcRequest Req(string command, params (string Key, string Value)[] args) =>
        new(command, args.Length == 0 ? null : args.ToDictionary(a => a.Key, a => a.Value));

    [Fact]
    public async Task Status_NoSession_Exit3()
    {
        await using var session = new RunSession(new FakeLauncher(), _log);

        var response = await Handler(session).HandleAsync(Req("status"), CancellationToken.None);

        Assert.Equal(ExitCodes.NoSession, response.ExitCode);
        Assert.False(response.Ok);
        Assert.NotNull(response.Data);
    }

    [Fact]
    public async Task Status_Running_Exit0_DataHasProjectConfigDevice()
    {
        var (session, _) = await RunningAsync(FakeProcessHandle.FromFixture("start-ok"));
        await using var lifetime = session;

        var response = await Handler(session).HandleAsync(Req("status"), CancellationToken.None);

        Assert.Equal(ExitCodes.Ok, response.ExitCode);
        Assert.True(response.Ok);
        var data = response.Data!.Value;
        Assert.Equal("Running", data.GetProperty("state").GetString());
        Assert.Equal("my-app", data.GetProperty("project").GetString());
        Assert.Equal("main", data.GetProperty("branch").GetString());
        Assert.Equal("beta", data.GetProperty("config").GetString());
        Assert.Equal("emulator-5554", data.GetProperty("device").GetString());
        Assert.Equal(0, data.GetProperty("errorCount").GetInt32());
    }

    [Fact]
    public async Task Status_Failed_Exit1_WithFailureMessage()
    {
        var handle = new FakeProcessHandle([], ["Gradle build failed"]);
        await using var session = new RunSession(new FakeLauncher(handle), _log);
        await session.StartAsync(Request());
        handle.Exit(1);
        await WaitStateAsync(session, SessionState.Failed);

        var response = await Handler(session).HandleAsync(Req("status"), CancellationToken.None);

        Assert.Equal(ExitCodes.Failed, response.ExitCode);
        Assert.Equal(session.Snapshot.FailureMessage, response.Message);
        Assert.NotNull(response.Data);
    }

    [Fact]
    public async Task Reload_Success_Exit0()
    {
        var (session, _) = await RunningAsync(FakeProcessHandle.FromFixture("reload-ok"));
        await using var lifetime = session;

        var response = await Handler(session).HandleAsync(Req("reload"), CancellationToken.None);

        Assert.Equal(ExitCodes.Ok, response.ExitCode);
        Assert.True(response.Data!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(response.Message, response.Data.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Restart_Success_Exit0()
    {
        var (session, handle) = await RunningAsync(FakeProcessHandle.FromFixture("restart-ok"));
        await using var lifetime = session;

        var response = await Handler(session).HandleAsync(Req("restart"), CancellationToken.None);

        Assert.Equal(ExitCodes.Ok, response.ExitCode);
        Assert.Contains("\"fullRestart\":true", Assert.Single(handle.Writes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reload_CompileError_Exit1_MessageHasError()
    {
        var (session, _) = await RunningAsync(FakeProcessHandle.FromFixture("reload-compile-error"));
        await using var lifetime = session;

        var response = await Handler(session).HandleAsync(Req("reload"), CancellationToken.None);

        Assert.Equal(ExitCodes.Failed, response.ExitCode);
        Assert.False(response.Ok);
        Assert.Contains("Expected an identifier", response.Message);
    }

    [Fact]
    public async Task Reload_WhileStarting_Exit3()
    {
        // Review Focus #4: appId known but app.started not yet seen.
        var handle = new FakeProcessHandle([AppStartLine()]);
        await using var session = new RunSession(new FakeLauncher(handle), _log);
        await session.StartAsync(Request());
        await handle.WhenIdle();

        var response = await Handler(session).HandleAsync(Req("reload"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ExitCodes.NoSession, response.ExitCode);
    }

    [Fact]
    public async Task Reload_WhileReloading_Exit1_ReloadInProgress()
    {
        var (session, handle) = await RunningAsync(FakeProcessHandle.FromFixture("start-ok"));
        await using var lifetime = session;
        var handler = Handler(session);
        var first = handler.HandleAsync(Req("reload"), CancellationToken.None);
        var writes = await handle.WaitForWritesAsync(1, Wait);
        var id = JsonDocument.Parse(writes[0]).RootElement[0].GetProperty("id").GetInt32();

        var second = await handler.HandleAsync(Req("reload"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ExitCodes.Failed, second.ExitCode);
        Assert.Equal("Reload in progress", second.Message);
        handle.EmitStdout($$$"""[{"id":{{{id}}},"result":{"code":0,"message":""}}]""");
        Assert.Equal(ExitCodes.Ok, (await first.WaitAsync(Wait)).ExitCode);
    }

    [Fact]
    public async Task Reload_Timeout_Exit4()
    {
        var (session, _) = await RunningAsync(FakeProcessHandle.FromFixture("start-ok"));
        await using var lifetime = session;

        var response = await Handler(session, TimeSpan.FromMilliseconds(100)).HandleAsync(Req("reload"), CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(ExitCodes.Timeout, response.ExitCode);
    }

    [Fact]
    public async Task Stop_NoSession_Exit3()
    {
        await using var session = new RunSession(new FakeLauncher(), _log);

        var response = await Handler(session).HandleAsync(Req("stop"), CancellationToken.None);

        Assert.Equal(ExitCodes.NoSession, response.ExitCode);
    }

    [Fact]
    public async Task Stop_Running_Exit0_BackToIdle()
    {
        var (session, _) = await RunningAsync(FakeProcessHandle.FromFixture("stop"));
        await using var lifetime = session;

        var response = await Handler(session).HandleAsync(Req("stop"), CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(ExitCodes.Ok, response.ExitCode);
        Assert.Equal(SessionState.Idle, session.Snapshot.State);
    }

    [Fact]
    public async Task StatusAndLogs_DuringSlowStop_AnswerAtOnce()
    {
        // flutter ignores app.stop: the stop waits the whole grace. A concurrent status/logs (CLI timeout 10 s) must
        // not queue behind it.
        var handle = FakeProcessHandle.FromFixture("start-ok");
        handle.AutoExitOnStop = false;
        var (session, _) = await RunningAsync(handle, stopGrace: TimeSpan.FromSeconds(3));
        await using var lifetime = session;
        var handler = Handler(session);

        var stop = handler.HandleAsync(Req("stop"), CancellationToken.None);
        await handle.WaitForWritesAsync(1, Wait); // app.stop sent: the stop grace is running
        var status = await handler.HandleAsync(Req("status"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        var logs = await handler.HandleAsync(Req("logs"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(ExitCodes.Ok, status.ExitCode); // Stopping still counts as a session
        Assert.Equal(ExitCodes.Ok, logs.ExitCode);
        Assert.False(stop.IsCompleted);
        Assert.Equal(ExitCodes.Ok, (await stop.WaitAsync(Wait)).ExitCode);
        Assert.Equal(1, handle.KillCount);
    }

    [Fact]
    public async Task Show_InvokesCallback()
    {
        await using var session = new RunSession(new FakeLauncher(), _log);

        var response = await Handler(session).HandleAsync(Req("show"), CancellationToken.None);

        Assert.Equal(ExitCodes.Ok, response.ExitCode);
        Assert.Equal(1, _shown);
    }

    [Fact]
    public async Task Logs_ErrorsSinceLastReload_FiltersBoth()
    {
        await using var session = new RunSession(new FakeLauncher(), _log);
        _log.Append(LogKind.Error, "old error");
        _log.MarkReload("Hot reload");
        _log.Append(LogKind.App, "hello");
        _log.Append(LogKind.Error, "new error 1");
        _log.Append(LogKind.Error, "new error 2");

        var response = await Handler(session).HandleAsync(
            Req("logs", ("errors", "true"), ("sinceLastReload", "true")), CancellationToken.None);

        Assert.Equal(ExitCodes.Ok, response.ExitCode);
        var lines = response.Data!.Value.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["new error 1", "new error 2"], lines.Select(l => l.GetProperty("text").GetString()));
        Assert.All(lines, l => Assert.Equal("error", l.GetProperty("kind").GetString()));
        Assert.True(lines[0].TryGetProperty("seq", out _));
        Assert.True(lines[0].TryGetProperty("time", out _));
    }

    [Fact]
    public async Task Logs_Tail_TakesLastN_AndWorksWhenIdle()
    {
        await using var session = new RunSession(new FakeLauncher(), _log);
        for (var i = 1; i <= 5; i++)
        {
            _log.Append(LogKind.App, $"line {i}");
        }

        var response = await Handler(session).HandleAsync(Req("logs", ("tail", "2")), CancellationToken.None);

        Assert.Equal(ExitCodes.Ok, response.ExitCode);
        var texts = response.Data!.Value.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString());
        Assert.Equal(["line 4", "line 5"], texts);
    }

    [Fact]
    public async Task Logs_InvalidTail_Exit64()
    {
        await using var session = new RunSession(new FakeLauncher(), _log);

        var response = await Handler(session).HandleAsync(Req("logs", ("tail", "abc")), CancellationToken.None);

        Assert.Equal(ExitCodes.Usage, response.ExitCode);
    }

    [Fact]
    public async Task UnknownCommand_Exit64()
    {
        await using var session = new RunSession(new FakeLauncher(), _log);

        var response = await Handler(session).HandleAsync(Req("bogus"), CancellationToken.None);

        Assert.Equal(ExitCodes.Usage, response.ExitCode);
        Assert.False(response.Ok);
    }
}
