using System.Collections.Concurrent;
using System.Threading.Channels;
using FlutterBar.Core.Diagnostics;

namespace FlutterBar.App.Diagnostics;

/// <summary>
/// Non-blocking front end for <see cref="DiagLog"/>. The widget is owned by the taskbar, which attaches the input
/// queues of both threads: the UI thread must never wait on file I/O, so entries are queued and written by one
/// background task. <see cref="Limited"/> drops repeats of the same key within a time window.
/// </summary>
internal static class AppLog
{
    private static readonly Channel<(string Message, Exception? Error)> Queue =
        Channel.CreateUnbounded<(string, Exception?)>(new UnboundedChannelOptions { SingleReader = true });

    private static readonly ConcurrentDictionary<string, DateTime> LastByKey = new();
    private static readonly Task Writer = Task.Run(WriteLoopAsync);

    public static void Write(string message, Exception? error = null)
    {
        if (!Queue.Writer.TryWrite((message, error)))
        {
            DiagLog.Write(message, error); // only after Flush (shutdown): nothing left to block
        }
    }

    /// <summary>Writes at most once per <paramref name="windowSeconds"/> for <paramref name="key"/>.</summary>
    public static void Limited(string key, string message, double windowSeconds = 2)
    {
        var now = DateTime.UtcNow;
        if (LastByKey.TryGetValue(key, out var last) && (now - last).TotalSeconds < windowSeconds)
        {
            return;
        }
        LastByKey[key] = now;
        if (LastByKey.Count > 512)
        {
            LastByKey.Clear();
        }
        Write(message);
    }

    /// <summary>Completes the queue and waits (bounded) for pending entries. Call once at shutdown, after the window is gone.</summary>
    public static void Flush(TimeSpan timeout)
    {
        Queue.Writer.TryComplete();
        if (!Writer.Wait(timeout))
        {
            DiagLog.Write($"AppLog: flush timed out after {timeout.TotalMilliseconds:F0} ms, pending entries lost");
        }
    }

    private static async Task WriteLoopAsync()
    {
        await foreach (var (message, error) in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            DiagLog.Write(message, error); // never throws (falls back to Trace)
        }
    }
}
