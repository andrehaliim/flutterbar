namespace StatefulBar.Core.Processes;

/// <summary><paramref name="Environment"/> entries are added to (or override) the child's inherited environment.</summary>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);

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

    /// <summary>
    /// Kills the process tree: the whole kill-on-close job when the handle owns one, otherwise the live process and its
    /// descendants (nothing once the process has exited, so daemons it detached survive).
    /// </summary>
    void KillTree();

    /// <summary>
    /// Hands the kill-on-close job to the caller (null without one, or when already detached): disposing the returned
    /// object kills everything still in the job. Afterwards <see cref="KillTree"/> and DisposeAsync only kill the live
    /// process tree, so daemons the process started (Gradle, adb) outlive the handle.
    /// </summary>
    IDisposable? DetachJob();
}

/// <summary>How a launcher contains the processes it starts (spec §5.3).</summary>
public enum ProcessContainment
{
    /// <summary>
    /// Kill-on-close Job Object: closing it, or StatefulBar exiting or crashing, kills every descendant. For the
    /// <c>flutter run</c> session.
    /// </summary>
    KillOnCloseJob,

    /// <summary>
    /// No job: killing ends the live process tree only, and daemons the command started (emulator, adb server, git
    /// fsmonitor) keep running after it exits. For one-shot commands (<see cref="ProcessRunner"/>).
    /// </summary>
    None,
}

public interface IProcessLauncher
{
    IProcessHandle Start(ProcessSpec spec);
}
