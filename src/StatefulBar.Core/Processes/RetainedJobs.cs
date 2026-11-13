namespace StatefulBar.Core.Processes;

/// <summary>
/// Kill-on-close jobs of run sessions whose flutter process exited on its own (a clean Stop, or a failed run). Keeping
/// them open lets the daemons that session started (Gradle, Kotlin, adb) stay warm for the next run (spec §6.2); they
/// still die when StatefulBar exits (<see cref="Dispose"/>) or crashes (the OS closes the job handles), per spec §5.3.
/// Thread-safe. A job is an empty kernel object once its processes are gone, so the list is not pruned.
/// </summary>
public sealed class RetainedJobs : IDisposable
{
    private readonly object _gate = new();
    private List<IDisposable>? _jobs = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _jobs?.Count ?? 0;
            }
        }
    }

    /// <summary>Keeps <paramref name="job"/> open until <see cref="Dispose"/>; after that it is disposed at once.</summary>
    public void Retain(IDisposable job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            if (_jobs is not null)
            {
                _jobs.Add(job);
                return;
            }
        }
        job.Dispose();
    }

    /// <summary>Closes every retained job, killing whatever still runs in them.</summary>
    public void Dispose()
    {
        List<IDisposable>? jobs;
        lock (_gate)
        {
            jobs = _jobs;
            _jobs = null;
        }
        foreach (var job in jobs ?? [])
        {
            job.Dispose();
        }
    }
}
