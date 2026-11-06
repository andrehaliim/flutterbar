namespace FlutterBar.Core.Processes;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Args, string WorkingDirectory);

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct);
}

public interface IProcessHandle : IAsyncDisposable
{
    int Id { get; }

    /// <summary>Completes with the exit code once the process exited and stdout/stderr were fully drained.</summary>
    Task<int> Exited { get; }

    /// <summary>
    /// Lines raised before the first subscriber attaches are buffered (newest 10,000 per stream) and replayed to it.
    /// Handlers run on a reader thread and must not throw.
    /// </summary>
    event Action<string>? StdoutLine;

    event Action<string>? StderrLine;

    /// <summary>Writes are serialized; do not call after <see cref="CloseStdin"/>.</summary>
    Task WriteLineAsync(string line);

    /// <summary>Closes the child's stdin so it sees EOF.</summary>
    void CloseStdin();

    void KillTree();
}

public interface IProcessLauncher
{
    IProcessHandle Start(ProcessSpec spec);
}
