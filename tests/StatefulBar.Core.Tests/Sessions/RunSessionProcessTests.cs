using System.Diagnostics;
using StatefulBar.Core.Config;
using StatefulBar.Core.Diagnostics;
using StatefulBar.Core.Logging;
using StatefulBar.Core.Processes;
using StatefulBar.Core.Sessions;
using StatefulBar.Core.Tests.Processes;

namespace StatefulBar.Core.Tests.Sessions;

/// <summary>
/// RunSession on real processes (EchoArgs <c>--fake-daemon</c>, which starts a long-lived child the way flutter starts
/// the Gradle daemon): Stop keeps that daemon only when flutter exits within the grace (spec §6.2, §5.3).
/// </summary>
[Collection("DiagLog")]
public sealed class RunSessionProcessTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly string _diagDir = Path.Combine(Path.GetTempPath(), "StatefulBarSessionProc-" + Guid.NewGuid().ToString("N"));
    private readonly LogBuffer _log = new();

    public RunSessionProcessTests()
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
            Trace.WriteLine("test cleanup failed: " + ex.Message);
        }
    }

    [Fact]
    public async Task CleanStop_LeavesSessionDaemonRunning_UntilRetainedJobsDisposed()
    {
        using var echo = new EchoArgsFixture();
        using var retained = new RetainedJobs();
        await using var session = new RunSession(new ProcessLauncher(), _log, TimeSpan.FromSeconds(10), retained);
        using var daemon = await StartAsync(session, echo, "--fake-daemon");
        try
        {
            await session.StopAsync().WaitAsync(Wait);

            Assert.Equal(SessionState.Idle, session.Snapshot.State);
            Assert.Equal(1, retained.Count);
            Assert.False(daemon.WaitForExit(TimeSpan.FromMilliseconds(500)), "a clean stop killed the session's daemon");

            retained.Dispose(); // FlutterBar exit

            await daemon.WaitForExitAsync().WaitAsync(Wait);
        }
        finally
        {
            ProcessLauncherTests.KillStarted(daemon);
        }
    }

    [Fact]
    public async Task Stop_FlutterIgnoresStop_KillsTreeIncludingDaemon()
    {
        using var echo = new EchoArgsFixture();
        using var retained = new RetainedJobs();
        await using var session = new RunSession(new ProcessLauncher(), _log, TimeSpan.FromMilliseconds(300), retained);
        using var daemon = await StartAsync(session, echo, "--fake-daemon", "--ignore-stop");
        try
        {
            await session.StopAsync().WaitAsync(Wait);

            await daemon.WaitForExitAsync().WaitAsync(Wait);
            Assert.Equal(0, retained.Count);
        }
        finally
        {
            ProcessLauncherTests.KillStarted(daemon);
        }
    }

    /// <summary>Starts the fake daemon, waits for Running and returns its long-lived child.</summary>
    private async Task<Process> StartAsync(RunSession session, EchoArgsFixture echo, params string[] args)
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += s =>
        {
            if (s.State == SessionState.Running)
            {
                running.TrySetResult();
            }
        };
        var request = new RunRequest(
            echo.Dir, new LaunchConfig("fake", "lib/main.dart", null, RunMode.Debug, []), "fake", echo.Spec(echo.Exe, args));

        await session.StartAsync(request);
        await running.Task.WaitAsync(Wait);

        var line = _log.Snapshot().Select(l => l.Text).Single(t => t.StartsWith("grandchild:", StringComparison.Ordinal));
        return Process.GetProcessById(int.Parse(line["grandchild:".Length..]));
    }
}
