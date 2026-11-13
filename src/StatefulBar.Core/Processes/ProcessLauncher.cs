using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using FlutterBar.Core.Diagnostics;

namespace FlutterBar.Core.Processes;

/// <summary>Starts processes with redirected stdio; <paramref name="containment"/> decides whether they go into a kill-on-close job.</summary>
public sealed class ProcessLauncher(ProcessContainment containment = ProcessContainment.KillOnCloseJob) : IProcessLauncher
{
    public ProcessContainment Containment { get; } = containment;

    internal const string CmdExe = "cmd.exe";

    internal static bool IsBatchFile(string fileName)
    {
        // Windows ignores trailing dots and spaces, so "x.bat." still runs as a batch file.
        var ext = Path.GetExtension(fileName.TrimEnd('.', ' '));
        return ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    public IProcessHandle Start(ProcessSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        ApplyCommand(psi, spec);
        if (spec.Environment is not null)
        {
            foreach (var (key, value) in spec.Environment)
            {
                psi.Environment[key] = value;
            }
        }

        var job = Containment == ProcessContainment.KillOnCloseJob ? new JobObject() : null;
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var started = false;
        try
        {
            process.Start();
            started = true;
            job?.Assign(process);
            return new Handle(process, job);
        }
        catch
        {
            // Startup failed: clean up, then let the original exception (e.g. Win32Exception) propagate.
            job?.Dispose();
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            process.Dispose();
            throw;
        }
    }

    private static void ApplyCommand(ProcessStartInfo psi, ProcessSpec spec)
    {
        if (IsBatchFile(spec.FileName))
        {
            psi.FileName = CmdExe;
            psi.Arguments = CmdEscaper.BuildCmdArguments(spec.FileName, spec.Args);
        }
        else if (IsCmdBatchInvocation(spec))
        {
            // cmd.exe <CmdSwitches> <batch> <args...> (as built by FlutterToolLocator): escape through CmdEscaper.
            var n = CmdEscaper.CmdSwitches.Count;
            psi.FileName = spec.FileName;
            psi.Arguments = CmdEscaper.BuildCmdArguments(spec.Args[n], [.. spec.Args.Skip(n + 1)]);
        }
        else
        {
            psi.FileName = spec.FileName;
            foreach (var arg in spec.Args)
            {
                psi.ArgumentList.Add(arg);
            }
        }
    }

    private static bool IsCmdBatchInvocation(ProcessSpec spec)
    {
        var switches = CmdEscaper.CmdSwitches;
        if (!Path.GetFileName(spec.FileName).Equals(CmdExe, StringComparison.OrdinalIgnoreCase)
            || spec.Args.Count <= switches.Count)
        {
            return false;
        }
        for (var i = 0; i < switches.Count; i++)
        {
            if (spec.Args[i] != switches[i])
            {
                return false;
            }
        }
        return IsBatchFile(spec.Args[switches.Count]);
    }

    /// <summary>
    /// Event with replay of lines raised before the first subscriber. Handlers are invoked outside the lock;
    /// the per-stream reader is serial, and lines arriving during replay are queued so order is preserved.
    /// </summary>
    private sealed class LineEvent
    {
        private const int MaxPending = 10_000;

        private readonly object _gate = new();
        private Queue<string>? _pending = new();
        private Action<string>? _handler;

        public void Raise(string line)
        {
            Action<string>? handler;
            lock (_gate)
            {
                if (_pending is not null)
                {
                    if (_pending.Count >= MaxPending)
                    {
                        _pending.Dequeue();
                    }
                    _pending.Enqueue(line);
                    return;
                }
                handler = _handler;
            }
            handler?.Invoke(line);
        }

        public void Add(Action<string> handler)
        {
            Queue<string>? batch;
            lock (_gate)
            {
                _handler += handler;
                if (_pending is null)
                {
                    return;
                }
                batch = _pending;
                _pending = new Queue<string>();
            }

            while (true)
            {
                foreach (var line in batch)
                {
                    handler(line);
                }
                lock (_gate)
                {
                    if (_pending!.Count == 0)
                    {
                        _pending = null;
                        return;
                    }
                    batch = _pending;
                    _pending = new Queue<string>();
                }
            }
        }

        public void Remove(Action<string> handler)
        {
            lock (_gate)
            {
                _handler -= handler;
            }
        }
    }

    private sealed class Handle : IProcessHandle
    {
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

        private readonly Process _process;
        private readonly object _processGate = new();
        private JobObject? _job;
        private bool _processDisposed;
        private readonly LineEvent _stdout = new();
        private readonly LineEvent _stderr = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposed;

        public Handle(Process process, JobObject? job)
        {
            _process = process;
            _job = job;
            Id = process.Id;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    _stdout.Raise(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    _stderr.Raise(e.Data);
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process.StandardInput.AutoFlush = true;
            Exited = WaitForExitAsync();
        }

        public int Id { get; }

        public Task<int> Exited { get; }

        public event Action<string>? StdoutLine
        {
            add => _stdout.Add(value!);
            remove => _stdout.Remove(value!);
        }

        public event Action<string>? StderrLine
        {
            add => _stderr.Add(value!);
            remove => _stderr.Remove(value!);
        }

        public async Task WriteLineAsync(string line)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void CloseStdin() => _process.StandardInput.Close();

        public void KillTree()
        {
            if (Volatile.Read(ref _job) is { } job)
            {
                job.Dispose();
                return;
            }
            lock (_processGate)
            {
                if (_processDisposed)
                {
                    return;
                }
                try
                {
                    // Only while it runs: once it exited, Kill(entireProcessTree) would still hunt down its former
                    // children, i.e. the daemons it detached on purpose.
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is AggregateException or Win32Exception or InvalidOperationException)
                {
                    // E.g. a descendant that exited or is not ours to kill; reported, the caller carries on.
                    DiagLog.Write($"ProcessLauncher: killing the process tree of pid {Id} failed", ex);
                }
            }
        }

        public IDisposable? DetachJob() => Interlocked.Exchange(ref _job, null);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            KillTree();
            var drained = await Task.WhenAny(Exited, Task.Delay(DrainTimeout)).ConfigureAwait(false) == Exited;
            if (drained)
            {
                lock (_processGate)
                {
                    _processDisposed = true;
                    _process.Dispose();
                }
            }
        }

        private async Task<int> WaitForExitAsync()
        {
            // Waits for the process to exit and for the redirected output streams to reach EOF.
            await _process.WaitForExitAsync().ConfigureAwait(false);
            return _process.ExitCode;
        }
    }
}
