using FlutterBar.Core.Config;
using FlutterBar.Core.Diagnostics;
using FlutterBar.Core.Logging;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Tests.Fakes;
using FlutterBar.Core.VmService;

namespace FlutterBar.Core.Tests.Sessions;

// Shares the DiagLog collection: error paths write to the (static) DiagLog, redirected to a temp file here.
[Collection("DiagLog")]
public sealed class RunSessionVmLogTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string WsUri = "ws://127.0.0.1:54178/token=/ws";

    private const string AppStartLine =
        """[{"event":"app.start","params":{"appId":"app-1","deviceId":"chrome","supportsRestart":true,"launchMode":"run"}}]""";
    private const string AppStartedLine = """[{"event":"app.started","params":{"appId":"app-1"}}]""";
    private const string DebugPortLine =
        """[{"event":"app.debugPort","params":{"appId":"app-1","port":54178,"wsUri":"ws://127.0.0.1:54178/token=/ws"}}]""";

    private readonly string _diagDir = Path.Combine(Path.GetTempPath(), "FlutterBarVmLog-" + Guid.NewGuid().ToString("N"));
    private readonly LogBuffer _log = new();
    private readonly FakeVmLogConnector _connector = new();

    public RunSessionVmLogTests()
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

    private static RunRequest Request() =>
        new(
            @"C:\proj",
            new LaunchConfig("beta", "lib/main.dart", null, RunMode.Debug, []),
            "chrome",
            new ProcessSpec("flutter", ["run", "--machine", "-d", "chrome"], @"C:\proj"));

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(10);
        }
    }

    private async Task<(RunSession Session, FakeProcessHandle Handle)> StartAsync(params string[] startup)
    {
        var handle = new FakeProcessHandle(startup);
        var session = new RunSession(new FakeLauncher(handle), _log, vmLogs: _connector);
        await session.StartAsync(Request());
        await UntilAsync(() => session.Snapshot.State == SessionState.Running);
        await handle.WhenIdle();
        return (session, handle);
    }

    private async Task<(RunSession Session, FakeProcessHandle Handle, FakeVmLogConnector.FakeConnection Connection)> StartConnectedAsync()
    {
        var (session, handle) = await StartAsync(AppStartLine, AppStartedLine, DebugPortLine);
        await UntilAsync(() => _connector.Connections.Count == 1);
        return (session, handle, _connector.Connections[0]);
    }

    private List<(LogKind Kind, string Text)> Lines() =>
        [.. _log.Snapshot().Where(l => l.Kind != LogKind.Separator).Select(l => (l.Kind, l.Text))];

    [Fact]
    public async Task DebugPort_ConnectsToTheVmService()
    {
        var (session, _, connection) = await StartConnectedAsync();
        await using var _ = session;

        Assert.Equal(new Uri(WsUri), connection.Uri);
        Assert.DoesNotContain(_log.Snapshot(), l => l.Text.Contains("token=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DebugPort_Repeated_ConnectsOnce()
    {
        var (session, handle, _) = await StartConnectedAsync();
        await using var _s = session;

        handle.EmitStdout(DebugPortLine);
        await handle.WhenIdle();

        Assert.Single(_connector.Connections);
    }

    [Theory]
    [InlineData("""[{"event":"app.debugPort","params":{"appId":"app-1","port":1}}]""")]
    [InlineData("""[{"event":"app.debugPort","params":{"appId":"app-1","wsUri":"http://127.0.0.1:1/ws"}}]""")]
    [InlineData("""[{"event":"app.debugPort","params":{"appId":"app-1","wsUri":"not a uri"}}]""")]
    public async Task DebugPort_WithoutUsableWsUri_DoesNotConnect(string debugPortLine)
    {
        var (session, handle) = await StartAsync(AppStartLine, AppStartedLine, debugPortLine);
        await using var _ = session;

        Assert.Empty(_connector.Connections);
        Assert.Empty(handle.HandlerFaults);
    }

    [Fact]
    public async Task WithoutConnector_DebugPortIsIgnored()
    {
        var handle = new FakeProcessHandle([AppStartLine, AppStartedLine, DebugPortLine]);
        await using var session = new RunSession(new FakeLauncher(handle), _log);
        await session.StartAsync(Request());
        await UntilAsync(() => session.Snapshot.State == SessionState.Running);
        await handle.WhenIdle();

        Assert.Empty(handle.HandlerFaults);
        Assert.Empty(Lines());
    }

    [Fact]
    public async Task LogRecord_IsAppendedWithLoggerName()
    {
        var (session, _, connection) = await StartConnectedAsync();
        await using var _ = session;

        connection.Emit(new VmLogRecord("demo", "hello", 0, null, null));
        connection.Emit(new VmLogRecord(string.Empty, "line one\nline two", 800, null, null));

        Assert.Equal(
            [(LogKind.App, "[demo] hello"), (LogKind.App, "[log] line one"), (LogKind.App, "line two")],
            Lines());
        Assert.False(session.Snapshot.HasError);
    }

    [Fact]
    public async Task LogRecord_WithError_IsAnErrorWithErrorAndStackTraceLines()
    {
        var (session, _, connection) = await StartConnectedAsync();
        await using var _ = session;

        connection.Emit(new VmLogRecord("net", "request failed", 0, "Bad state: boom", "#0 main (file.dart:1)\n#1 run (file.dart:2)\n"));

        Assert.Equal(
            [
                (LogKind.Error, "[net] request failed"),
                (LogKind.Error, "Bad state: boom"),
                (LogKind.Error, "#0 main (file.dart:1)"),
                (LogKind.Error, "#1 run (file.dart:2)"),
            ],
            Lines());
        Assert.True(session.Snapshot.HasError);
    }

    [Theory]
    [InlineData(999, LogKind.App)]
    [InlineData(1000, LogKind.Error)]
    [InlineData(1200, LogKind.Error)]
    public async Task LogRecord_SevereLevel_IsAnError(int level, LogKind expected)
    {
        var (session, _, connection) = await StartConnectedAsync();
        await using var _ = session;

        connection.Emit(new VmLogRecord("demo", "msg", level, null, null));

        Assert.Equal([(expected, "[demo] msg")], Lines());
        Assert.Equal(expected == LogKind.Error, session.Snapshot.HasError);
    }

    [Fact]
    public async Task Stop_ClosesTheConnection_AndLaterRecordsAreDropped()
    {
        var (session, _, connection) = await StartConnectedAsync();
        await using var _ = session;

        await session.StopAsync().WaitAsync(Wait);
        connection.Emit(new VmLogRecord("demo", "late", 0, null, null));
        connection.Fail("late failure");

        Assert.True(connection.DisposeCount >= 1);
        Assert.Empty(Lines());
        Assert.Equal(SessionState.Idle, session.Snapshot.State);
    }

    [Fact]
    public async Task ProcessExit_ClosesTheConnection()
    {
        var (session, handle, connection) = await StartConnectedAsync();
        await using var _ = session;

        handle.Exit(1);
        await UntilAsync(() => session.Snapshot.State == SessionState.Failed);

        Assert.True(connection.DisposeCount >= 1);
    }

    [Fact]
    public async Task ConnectionFailure_IsReportedInTheLog_AndTheSessionKeepsRunning()
    {
        var (session, _, connection) = await StartConnectedAsync();
        await using var _ = session;

        connection.Fail("the connection timed out");

        Assert.Equal([(LogKind.Tool, "log() output from dart:developer won't be shown: the connection timed out")], Lines());
        Assert.Equal(SessionState.Running, session.Snapshot.State);
        Assert.False(session.Snapshot.HasError);
    }

    [Fact]
    public async Task ConnectorThrowing_IsReportedInTheLog_AndTheSessionKeepsRunning()
    {
        _connector.ConnectException = new InvalidOperationException("no sockets");

        var (session, handle) = await StartAsync(AppStartLine, AppStartedLine, DebugPortLine);
        await using var _ = session;
        await UntilAsync(() => Lines().Count == 1);

        Assert.Equal([(LogKind.Tool, "log() output from dart:developer won't be shown: no sockets")], Lines());
        Assert.Equal(SessionState.Running, session.Snapshot.State);
        Assert.Empty(handle.HandlerFaults);
    }
}
