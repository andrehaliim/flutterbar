using StatefulBar.Core.Processes;

namespace StatefulBar.Core.Tests.Fakes;

/// <summary>Hands out queued <see cref="FakeProcessHandle"/>s (or throws <see cref="StartException"/>) and records specs.</summary>
public sealed class FakeLauncher : IProcessLauncher
{
    private readonly object _gate = new();
    private readonly Queue<FakeProcessHandle> _handles = new();
    private readonly List<ProcessSpec> _starts = [];

    public FakeLauncher(params FakeProcessHandle[] handles)
    {
        foreach (var handle in handles)
        {
            _handles.Enqueue(handle);
        }
    }

    /// <summary>When set, <see cref="Start"/> throws it (e.g. executable not found).</summary>
    public Exception? StartException { get; set; }

    public IReadOnlyList<ProcessSpec> Starts
    {
        get
        {
            lock (_gate)
            {
                return [.. _starts];
            }
        }
    }

    public void Enqueue(FakeProcessHandle handle)
    {
        lock (_gate)
        {
            _handles.Enqueue(handle);
        }
    }

    public IProcessHandle Start(ProcessSpec spec)
    {
        FakeProcessHandle handle;
        lock (_gate)
        {
            _starts.Add(spec);
            if (StartException is { } ex)
            {
                throw ex;
            }
            handle = _handles.Count > 0
                ? _handles.Dequeue()
                : throw new InvalidOperationException("FakeLauncher: no scripted handle left");
        }
        handle.Begin(spec);
        return handle;
    }
}
