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

    /// <summary>Lines raised before the first subscriber attaches are buffered and replayed to it.</summary>
    event Action<string>? StdoutLine;

    event Action<string>? StderrLine;

    Task WriteLineAsync(string line);

    void KillTree();
}

public interface IProcessLauncher
{
    IProcessHandle Start(ProcessSpec spec);
}
