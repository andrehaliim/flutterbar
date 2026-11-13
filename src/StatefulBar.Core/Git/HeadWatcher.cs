namespace FlutterBar.Core.Git;

/// <summary>
/// Watches a git HEAD file and raises <see cref="Changed"/> (debounced 300 ms) when it is modified.
/// Git replaces HEAD by renaming HEAD.lock over it, so Changed, Created and Renamed (new name == HEAD) all count.
/// </summary>
public sealed class HeadWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly string _fileName;
    private volatile bool _disposed;

    public HeadWatcher(string headFilePath)
    {
        var full = Path.GetFullPath(headFilePath);
        _fileName = Path.GetFileName(full);
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(full)!, _fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += (_, _) => Schedule();
        _watcher.Created += (_, _) => Schedule();
        _watcher.Renamed += (_, e) =>
        {
            if (string.Equals(e.Name, _fileName, StringComparison.OrdinalIgnoreCase))
            {
                Schedule();
            }
        };
        // Not swallowed: on a watcher error (e.g. buffer overflow) events may have been lost, so
        // signal Changed anyway and let the UI re-read the current branch.
        _watcher.Error += (_, _) => Schedule();
        _watcher.EnableRaisingEvents = true;
    }

    public event Action? Changed;

    public void Dispose()
    {
        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }

    private void Schedule()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan); // resets the window on every event
        }
        catch (ObjectDisposedException)
        {
            // raced with Dispose
        }
    }

    private void Fire()
    {
        if (!_disposed)
        {
            Changed?.Invoke();
        }
    }
}
