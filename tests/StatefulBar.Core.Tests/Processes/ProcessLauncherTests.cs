using System.Diagnostics;
using System.Threading.Channels;
using StatefulBar.Core.Processes;

namespace StatefulBar.Core.Tests.Processes;

public class ProcessLauncherTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static async Task<string> NextLine(ChannelReader<string> reader)
    {
        using var cts = new CancellationTokenSource(Wait);
        return await reader.ReadAsync(cts.Token);
    }

    /// <summary>Test cleanup for a process this test started (directly or through EchoArgs).</summary>
    internal static void KillStarted(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExit(Wait);
        }
    }

    private static Channel<string> SubscribeStdout(IProcessHandle handle)
    {
        var ch = Channel.CreateUnbounded<string>();
        handle.StdoutLine += l => ch.Writer.TryWrite(l);
        return ch;
    }

    [Fact]
    public async Task Launcher_StreamsStdout_AndWritesStdin()
    {
        using var echo = new EchoArgsFixture();
        await using var handle = new ProcessLauncher().Start(echo.Spec(echo.Exe, "--interactive"));
        var lines = SubscribeStdout(handle);

        await handle.WriteLineAsync("hello");
        Assert.Equal("echo:hello", await NextLine(lines.Reader));
        await handle.WriteLineAsync("dunia ü");
        Assert.Equal("echo:dunia ü", await NextLine(lines.Reader));
    }

    [Fact]
    public async Task Exited_CompletesAfterOutputDrained()
    {
        using var echo = new EchoArgsFixture();
        var handle = new ProcessLauncher().Start(echo.Spec(echo.Exe, "a", "b"));
        var seen = new List<string>();
        handle.StdoutLine += seen.Add;

        var code = await handle.Exited.WaitAsync(Wait);
        await handle.DisposeAsync();

        Assert.Equal(0, code);
        Assert.Equal(["[\"a\",\"b\"]"], seen);
    }

    [Fact]
    public async Task KillTree_KillsGrandchildren()
    {
        using var echo = new EchoArgsFixture();
        await using var handle = new ProcessLauncher().Start(echo.Spec(echo.Exe, "--spawn-child"));
        var lines = SubscribeStdout(handle);

        var childPid = int.Parse(await NextLine(lines.Reader));
        using var child = Process.GetProcessById(childPid);
        Assert.False(child.HasExited);

        handle.KillTree();

        await child.WaitForExitAsync().WaitAsync(Wait);
        Assert.True(child.HasExited);
        await handle.Exited.WaitAsync(Wait);
    }

    [Fact]
    public async Task DisposeLauncher_KillsJob()
    {
        using var echo = new EchoArgsFixture();
        var handle = new ProcessLauncher().Start(echo.Spec(echo.Exe, "--spawn-child"));
        var lines = SubscribeStdout(handle);
        var childPid = int.Parse(await NextLine(lines.Reader));
        using var child = Process.GetProcessById(childPid);
        using var parent = Process.GetProcessById(handle.Id);

        await handle.DisposeAsync();

        await parent.WaitForExitAsync().WaitAsync(Wait);
        await child.WaitForExitAsync().WaitAsync(Wait);
        Assert.True(parent.HasExited);
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task KillTree_ExitCodeIsNonZero()
    {
        using var echo = new EchoArgsFixture();
        await using var handle = new ProcessLauncher().Start(echo.Spec(echo.Exe, "--interactive"));

        handle.KillTree();

        Assert.NotEqual(0, await handle.Exited.WaitAsync(Wait));
    }

    [Fact]
    public void Start_MissingExe_SurfacesOriginalWin32Exception()
    {
        var spec = new ProcessSpec(Path.Combine(Path.GetTempPath(), "no-such-statefulbar-tool.exe"), [], Path.GetTempPath());

        Assert.Throws<System.ComponentModel.Win32Exception>(() => new ProcessLauncher().Start(spec));
    }

    [Fact]
    public void Start_MissingWorkingDirectory_SurfacesOriginalWin32Exception()
    {
        using var echo = new EchoArgsFixture();
        var missing = Path.Combine(echo.Dir, "deleted-project");

        Assert.Throws<System.ComponentModel.Win32Exception>(
            () => new ProcessLauncher().Start(new ProcessSpec(echo.Exe, [], missing)));
    }

    [Fact]
    public async Task Runner_ClosesStdin_SoInteractiveChildExitsInsteadOfTimingOut()
    {
        using var echo = new EchoArgsFixture();

        var result = await new ProcessRunner()
            .RunAsync(echo.Spec(echo.Exe, "--interactive"), TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Runner_Timeout_KillsAndReturnsMinusOne()
    {
        using var echo = new EchoArgsFixture();
        var result = await new ProcessRunner()
            .RunAsync(echo.Spec(echo.Exe, "--hang"), TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task Runner_Cancellation_ThrowsOperationCanceled()
    {
        using var echo = new EchoArgsFixture();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner()
            .RunAsync(echo.Spec(echo.Exe, "--hang"), TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task Runner_ChildExits_DaemonItStartedKeepsRunning()
    {
        // "flutter emulators --launch" / "adb start-server": the command exits, the emulator / adb server it started
        // must survive the runner (no kill-on-close job for one-shot commands).
        using var echo = new EchoArgsFixture();

        var result = await new ProcessRunner()
            .RunAsync(echo.Spec(echo.Exe, "--spawn-detached-child"), Wait, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        using var daemon = Process.GetProcessById(int.Parse(result.Stdout.Trim()));
        try
        {
            Assert.False(daemon.WaitForExit(TimeSpan.FromMilliseconds(500)), "the daemon was killed with the command");
        }
        finally
        {
            KillStarted(daemon);
        }
    }

    [Fact]
    public async Task WithoutJob_KillTree_KillsTheLiveProcessTree()
    {
        using var echo = new EchoArgsFixture();
        var handle = new ProcessLauncher(ProcessContainment.None).Start(echo.Spec(echo.Exe, "--spawn-child"));
        var lines = SubscribeStdout(handle);
        var childPid = int.Parse(await NextLine(lines.Reader));
        using var child = Process.GetProcessById(childPid);

        handle.KillTree();

        await child.WaitForExitAsync().WaitAsync(Wait);
        Assert.NotEqual(0, await handle.Exited.WaitAsync(Wait));
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task DetachJob_ThenDispose_LeavesTreeAlive_UntilJobDisposed()
    {
        using var echo = new EchoArgsFixture();
        var handle = new ProcessLauncher().Start(echo.Spec(echo.Exe, "--spawn-detached-child"));
        var lines = SubscribeStdout(handle);
        using var daemon = Process.GetProcessById(int.Parse(await NextLine(lines.Reader)));
        try
        {
            var job = handle.DetachJob();
            Assert.NotNull(job);
            Assert.Null(handle.DetachJob());

            handle.CloseStdin(); // the parent exits on EOF, as flutter does after app.stop
            Assert.Equal(0, await handle.Exited.WaitAsync(Wait));
            await handle.DisposeAsync();
            Assert.False(daemon.WaitForExit(TimeSpan.FromMilliseconds(500)), "disposing the handle killed the detached job");

            job.Dispose();
            await daemon.WaitForExitAsync().WaitAsync(Wait);
        }
        finally
        {
            KillStarted(daemon);
        }
    }

    [Fact]
    public async Task Runner_NonZeroExit_AndStderr_AreCaptured()
    {
        var spec = new ProcessSpec("cmd.exe", ["/d", "/s", "/c", "echo oops 1>&2 & exit 3"], Environment.CurrentDirectory);

        var result = await new ProcessRunner().RunAsync(spec, Wait, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("oops", result.Stderr);
    }
}
