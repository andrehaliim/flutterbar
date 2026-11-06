using System.Text;

namespace FlutterBar.Core.Processes;

public sealed class ProcessRunner(IProcessLauncher launcher) : IProcessRunner
{
    public ProcessRunner()
        : this(new ProcessLauncher())
    {
    }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        await using var handle = launcher.Start(spec);
        handle.StdoutLine += line => Append(stdout, line);
        handle.StderrLine += line => Append(stderr, line);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            var exitCode = await handle.Exited.WaitAsync(linked.Token).ConfigureAwait(false);
            return Result(exitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Timed out (not cancelled by the caller): kill the tree and report what was captured.
            handle.KillTree();
            await handle.Exited.ConfigureAwait(false);
            return Result(-1, stdout, stderr);
        }
        // Caller cancellation propagates; the handle's DisposeAsync kills the process tree.
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
