using System.Diagnostics;
using System.Threading.Channels;
using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Tests.Processes;

public class ProcessLauncherTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static async Task<string> NextLine(ChannelReader<string> reader)
    {
        using var cts = new CancellationTokenSource(Wait);
        return await reader.ReadAsync(cts.Token);
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
    public async Task Runner_Timeout_KillsAndReturnsMinusOne()
    {
        using var echo = new EchoArgsFixture();
        var result = await new ProcessRunner(new ProcessLauncher())
            .RunAsync(echo.Spec(echo.Exe, "--interactive"), TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task Runner_Cancellation_ThrowsOperationCanceled()
    {
        using var echo = new EchoArgsFixture();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner(new ProcessLauncher())
            .RunAsync(echo.Spec(echo.Exe, "--interactive"), TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task Runner_NonZeroExit_AndStderr_AreCaptured()
    {
        var spec = new ProcessSpec("cmd.exe", ["/d", "/s", "/c", "echo oops 1>&2 & exit 3"], Environment.CurrentDirectory);

        var result = await new ProcessRunner(new ProcessLauncher()).RunAsync(spec, Wait, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("oops", result.Stderr);
    }
}
