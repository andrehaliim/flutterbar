using System.Text;

namespace StatefulBar.Core.Processes;

/// <summary>
/// Runs one-shot commands (flutter devices / emulators, git, adb) to completion. They are not put into a kill-on-close
/// job: daemons a command starts (the emulator from <c>flutter emulators --launch</c>, the adb server, git fsmonitor) must
/// keep running after it exits (spec §5.4). On timeout or cancellation the live process tree is killed.
/// </summary>
public sealed class ProcessRunner(IProcessLauncher launcher) : IProcessRunner
{
    public ProcessRunner()
        : this(new ProcessLauncher(ProcessContainment.None))
    {
    }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        await using var handle = launcher.Start(spec);
        handle.StdoutLine += line => Append(stdout, line);
        handle.StderrLine += line => Append(stderr, line);
        handle.CloseStdin(); // commands run here are non-interactive: prompts see EOF instead of hanging

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            var exitCode = await handle.Exited.WaitAsync(linked.Token).ConfigureAwait(false);
            return Result(exitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Timed out (not cancelled by the caller): kill the live tree and report what was captured.
            handle.KillTree();
            // Bounded wait for the output to drain; if it does not, report what we have.
            await Task.WhenAny(handle.Exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            return Result(-1, stdout, stderr);
        }
        // Caller cancellation propagates; the handle's DisposeAsync kills the live process tree.
    }

    private static void Append(StringBuilder sb, string line)
    {
        lock (sb)
        {
            sb.Append(line).Append('\n');
        }
    }

    private static ProcessResult Result(int exitCode, StringBuilder stdout, StringBuilder stderr)
    {
        lock (stdout)
        {
            lock (stderr)
            {
                return new ProcessResult(exitCode, stdout.ToString(), stderr.ToString());
            }
        }
    }
}
