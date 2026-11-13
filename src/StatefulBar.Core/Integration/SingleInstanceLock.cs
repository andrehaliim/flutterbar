using StatefulBar.Core.Diagnostics;
using StatefulBar.Core.Ipc;

namespace StatefulBar.Core.Integration;

/// <summary>
/// One FlutterBar.App per user session (spec §10): a named mutex <c>Local\FlutterBar-&lt;SID&gt;</c>. It must be acquired
/// before the IPC server starts, so only the owner of the mutex can ever listen on the pipe. A mutex is owned by the
/// thread that acquired it: dispose on that same thread (the app's UI thread).
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceLock(Mutex mutex) => _mutex = mutex;

    public static string NameForCurrentUser() => @"Local\" + PipeNames.ForCurrentUser();

    /// <summary>
    /// Null when another instance holds the mutex. An abandoned mutex (previous instance crashed) counts as acquired,
    /// reported through <paramref name="abandoned"/>.
    /// </summary>
    public static SingleInstanceLock? TryAcquire(string name, out bool abandoned)
    {
        abandoned = false;
        var mutex = new Mutex(false, name);
        try
        {
            if (mutex.WaitOne(0))
            {
                return new SingleInstanceLock(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            abandoned = true; // ownership was transferred to this thread
            return new SingleInstanceLock(mutex);
        }
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException ex) // not called on the owning thread: the OS releases it when the process ends
        {
            DiagLog.Write("single instance: mutex release from a non-owning thread", ex);
        }
        _mutex.Dispose();
    }
}
