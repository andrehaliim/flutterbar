using System.Text.Json;
using System.Text.RegularExpressions;
using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Tests.Fakes;

/// <summary>
/// Scripted stand-in for a <c>flutter run --machine</c> process.
/// <list type="bullet">
/// <item>On start (via <see cref="FakeLauncher"/>) the startup stdout/stderr lines are emitted.</item>
/// <item>Each request written to stdin dequeues the next scripted segment: its stderr lines first, then its stdout
/// lines, with the <c>"id"</c> of every response line rewritten to the id of that request.</item>
/// <item>An <c>app.stop</c> request makes the process exit with <see cref="StopExitCode"/> when
/// <see cref="AutoExitOnStop"/> is set; <see cref="KillTree"/> exits with -1 when <see cref="ExitOnKill"/> is set.</item>
/// </list>
/// Emission is asynchronous (like the real reader threads) but strictly ordered; <see cref="Exited"/> completes only
/// after all previously scheduled lines were raised. Lines raised before the first subscriber are replayed to it.
/// </summary>
public sealed partial class FakeProcessHandle : IProcessHandle
{
    private readonly object _gate = new();
    private readonly FakeLineEvent _stdout = new();
    private readonly FakeLineEvent _stderr = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<Segment> _segments = new();
    private readonly List<string> _writes = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> _writeWaiters = [];
    private readonly List<Exception> _handlerFaults = [];
    private readonly IReadOnlyList<string> _startupStdout;
    private readonly IReadOnlyList<string> _startupStderr;
    private Task _chain = Task.CompletedTask;
    private int _killCount;
    private int _jobDetached;

    public FakeProcessHandle(IEnumerable<string>? startupStdout = null, IEnumerable<string>? startupStderr = null, int id = 4242)
    {
        _startupStdout = [.. startupStdout ?? []];
        _startupStderr = [.. startupStderr ?? []];
        Id = id;
    }

    public int Id { get; }

    public Task<int> Exited => _exited.Task;

    public ProcessSpec? Spec { get; private set; }

    /// <summary>Exit with <see cref="StopExitCode"/> after answering an <c>app.stop</c> request.</summary>
    public bool AutoExitOnStop { get; set; } = true;

    public int StopExitCode { get; set; }

    /// <summary>Exit with -1 when <see cref="KillTree"/> is called.</summary>
    public bool ExitOnKill { get; set; } = true;

    public int KillCount => Volatile.Read(ref _killCount);

    public bool StdinClosed { get; private set; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<string> Writes
    {
        get
        {
            lock (_gate)
            {
                return [.. _writes];
            }
        }
    }

    /// <summary>Exceptions thrown by subscribers (the real launcher would crash on these). Tests assert it is empty.</summary>
    public IReadOnlyList<Exception> HandlerFaults
    {
        get
        {
            lock (_gate)
            {
                return [.. _handlerFaults];
            }
        }
    }

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

    /// <summary>
    /// Script from a recorded fixture: stdout up to and including <c>app.started</c> (or everything when the remaining
    /// lines hold no response) is the startup output; the rest is split into one segment per response line, trailing
    /// lines joining the last segment. The stderr sidecar goes with the first segment, or with startup when there is none.
    /// </summary>
    public static FakeProcessHandle FromFixture(string name, string version = "3.22.3")
    {
        var stdout = File.ReadAllLines(TestPaths.Fixture("daemon", version, name + ".jsonl"));
        var stderrPath = TestPaths.Fixture("daemon", version, name + ".stderr.txt");
        var stderr = File.Exists(stderrPath) ? File.ReadAllLines(stderrPath) : [];

        var startedAt = Array.FindIndex(stdout, l => l.Contains("\"event\":\"app.started\"", StringComparison.Ordinal));
        var startupCount = startedAt < 0 ? stdout.Length : startedAt + 1;
        var rest = stdout[startupCount..];
        if (!rest.Any(IsResponse))
        {
            return new FakeProcessHandle(stdout, stderr);
        }

        var handle = new FakeProcessHandle(stdout[..startupCount]);
        var current = new List<string>();
        var segments = new List<List<string>>();
        foreach (var line in rest)
        {
            current.Add(line);
            if (IsResponse(line))
            {
                segments.Add(current);
                current = [];
            }
        }
        segments[^1].AddRange(current);
        for (var i = 0; i < segments.Count; i++)
        {
            handle.EnqueueSegment(segments[i], i == 0 ? stderr : null);
        }
        return handle;
    }

    /// <summary>Adds the output to emit for the next request (responses get that request's id).</summary>
    public void EnqueueSegment(IEnumerable<string> stdout, IEnumerable<string>? stderr = null)
    {
        lock (_gate)
        {
            _segments.Enqueue(new Segment([.. stdout], [.. stderr ?? []]));
        }
    }

    public void EmitStdout(params string[] lines) => Schedule(() => Raise(_stdout, lines));

    public void EmitStderr(params string[] lines) => Schedule(() => Raise(_stderr, lines));

    /// <summary>Completes <see cref="Exited"/> after every line scheduled so far was raised.</summary>
    public void Exit(int code) => Schedule(() => _exited.TrySetResult(code));

    /// <summary>Completes once every emission scheduled so far has been raised.</summary>
    public Task WhenIdle()
    {
        lock (_gate)
        {
            return _chain;
        }
    }

    public async Task<IReadOnlyList<string>> WaitForWritesAsync(int count, TimeSpan timeout)
    {
        Task signal;
        lock (_gate)
        {
            if (_writes.Count >= count)
            {
                return [.. _writes];
            }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _writeWaiters.Add((count, tcs));
            signal = tcs.Task;
        }
        await signal.WaitAsync(timeout);
        return Writes;
    }

    public Task WriteLineAsync(string line)
    {
        if (StdinClosed || Disposed)
        {
            throw new InvalidOperationException("stdin is closed");
        }

        Segment? segment = null;
        int? requestId = null;
        string? method = null;
        lock (_gate)
        {
            _writes.Add(line);
            foreach (var waiter in _writeWaiters.Where(w => _writes.Count >= w.Count).ToList())
            {
                waiter.Signal.TrySetResult();
                _writeWaiters.Remove(waiter);
            }
            (requestId, method) = ParseRequest(line);
            if (requestId is not null && _segments.Count > 0)
            {
                segment = _segments.Dequeue();
            }
        }

        if (segment is not null && requestId is int id)
        {
            var stdout = segment.Stdout.Select(l => IsResponse(l) ? ResponseId().Replace(l, $"[{{\"id\":{id}", 1) : l).ToArray();
            EmitStderr([.. segment.Stderr]);
            EmitStdout(stdout);
        }
        if (method == "app.stop" && AutoExitOnStop)
        {
            Exit(StopExitCode);
        }
        return Task.CompletedTask;
    }

    public void CloseStdin() => StdinClosed = true;

    public void KillTree()
    {
        Interlocked.Increment(ref _killCount);
        if (ExitOnKill)
        {
            Exit(-1);
        }
    }

    /// <summary>The kill-on-close job this fake process "runs in"; handed out once by <see cref="DetachJob"/>.</summary>
    public FakeJob Job { get; } = new();

    public bool JobDetached => Volatile.Read(ref _jobDetached) != 0;

    public IDisposable? DetachJob() => Interlocked.Exchange(ref _jobDetached, 1) == 0 ? Job : null;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Called by <see cref="FakeLauncher"/>: emits the startup output.</summary>
    internal void Begin(ProcessSpec spec)
    {
        Spec = spec;
        EmitStdout([.. _startupStdout]);
        EmitStderr([.. _startupStderr]);
    }

    private static bool IsResponse(string line) => ResponseId().IsMatch(line);

    private static (int? Id, string? Method) ParseRequest(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var obj = doc.RootElement[0];
        var id = obj.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : (int?)null;
        var method = obj.TryGetProperty("method", out var m) ? m.GetString() : null;
        return (id, method);
    }

    private void Schedule(Action action)
    {
        lock (_gate)
        {
            _chain = _chain.ContinueWith(
                _ =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        // Surfaced through HandlerFaults; tests assert it stays empty.
                        lock (_gate)
                        {
                            _handlerFaults.Add(ex);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private static void Raise(FakeLineEvent target, string[] lines)
    {
        foreach (var line in lines)
        {
            target.Raise(line);
        }
    }

    [GeneratedRegex("^\\[\\{\"id\":\\d+")]
    private static partial Regex ResponseId();

    public sealed class FakeJob : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed record Segment(IReadOnlyList<string> Stdout, IReadOnlyList<string> Stderr);

    /// <summary>Same replay semantics as the real launcher: lines raised before the first subscriber are queued.</summary>
    private sealed class FakeLineEvent
    {
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
                    _pending.Enqueue(line);
                    return;
                }
                handler = _handler;
            }
            handler?.Invoke(line);
        }

        public void Add(Action<string> handler)
        {
            Queue<string> batch;
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
                    if (_pending.Count == 0)
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
}
