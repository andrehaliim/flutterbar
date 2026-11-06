using System.Diagnostics;
using System.Text;

namespace FlutterBar.Core.Processes;

public sealed class ProcessLauncher : IProcessLauncher
{
    internal const string CmdExe = "cmd.exe";

    internal static bool IsBatchFile(string fileName)
    {
        var ext = Path.GetExtension(fileName);
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

        var job = new JobObject();
        Process? process = null;
        try
        {
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
            job.Assign(process);
            return new Handle(process, job);
        }
        catch
        {
            // Startup failed: make sure nothing is left running or leaked, then surface the original error.
            job.Dispose();
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
            process?.Dispose();
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
            // cmd.exe /d /s /c <batch> <args...> (as built by FlutterToolLocator): escape through CmdEscaper.
            psi.FileName = spec.FileName;
            psi.Arguments = CmdEscaper.BuildCmdArguments(spec.Args[3], [.. spec.Args.Skip(4)]);
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

    private static bool IsCmdBatchInvocation(ProcessSpec spec) =>
        Path.GetFileName(spec.FileName).Equals(CmdExe, StringComparison.OrdinalIgnoreCase)
        && spec.Args.Count >= 4
        && spec.Args[0] == "/d"
        && spec.Args[1] == "/s"
        && spec.Args[2] == "/c"
        && IsBatchFile(spec.Args[3]);

    private sealed class LineEvent
    {
        private readonly object _gate = new();
        private List<string>? _pending = [];
        private Action<string>? _handler;

        public void Raise(string line)
        {
            lock (_gate)
            {
                if (_handler is null)
                {
                    _pending?.Add(line);
                    return;
                }
                _handler(line);
            }
        }

        public void Add(Action<string> handler)
        {
            lock (_gate)
            {
                _handler += handler;
                if (_pending is { } pending)
                {
                    _pending = null;
                    foreach (var line in pending)
                    {
                        handler(line);
                    }
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
        private readonly Process _process;
        private readonly JobObject _job;
        private readonly LineEvent _stdout = new();
        private readonly LineEvent _stderr = new();
        private int _disposed;

        public Handle(Process process, JobObject job)
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

        public Task WriteLineAsync(string line) => _process.StandardInput.WriteLineAsync(line);

        public void KillTree() => _job.Dispose();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            KillTree();
            await Exited.ConfigureAwait(false);
            _process.Dispose();
        }

        private async Task<int> WaitForExitAsync()
        {
            // Waits for the process to exit and for the redirected output streams to reach EOF.
            await _process.WaitForExitAsync().ConfigureAwait(false);
            return _process.ExitCode;
        }
    }
}
