namespace FlutterBar.Core.Logging;

public enum LogKind
{
    Tool,
    App,
    Error,
    Reload,
    Separator,
}

public sealed record LogLine(long Seq, DateTimeOffset Timestamp, LogKind Kind, string Text);

/// <summary>Thread-safe ring buffer of log lines with monotonically increasing sequence numbers.</summary>
public sealed class LogBuffer
{
    private readonly object _gate = new();
    private readonly LogLine?[] _items;
    private readonly TimeProvider _time;
    private int _start;
    private int _count;
    private long _nextSeq = 1;
    private long _lastReloadSeq;

    public LogBuffer(int capacity = 20_000, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new LogLine?[capacity];
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised after a line is appended, outside the internal lock. Handlers must not throw;
    /// an exception propagates to the caller of <see cref="Append"/>.
    /// </summary>
    public event Action<LogLine>? Appended;

    public LogLine Append(LogKind kind, string text) => AppendCore(kind, text, isMarker: false);

    public void MarkReload(string label) => AppendCore(LogKind.Separator, Separator(label), isMarker: true);

    /// <summary>Separator before a one-off command's output (e.g. <c>flutter pub get</c>); the reload marker is not moved.</summary>
    public LogLine MarkCommand(string label) => AppendCore(LogKind.Separator, Separator(label), isMarker: false);

    /// <summary>Starts a new session segment; resets the reload marker to this separator.</summary>
    public void MarkSessionStart(string label) => AppendCore(LogKind.Separator, Separator(label), isMarker: true);

    /// <summary>Lines with <c>Seq</c> greater than <paramref name="seqExclusive"/>.</summary>
    public IReadOnlyList<LogLine> Since(long seqExclusive)
    {
        lock (_gate)
        {
            return Collect(l => l.Seq > seqExclusive);
        }
    }

    /// <summary>Lines after the last reload/session-start separator (separator excluded).</summary>
    public IReadOnlyList<LogLine> SinceLastReload()
    {
        lock (_gate)
        {
            var marker = _lastReloadSeq;
            return Collect(l => l.Seq > marker);
        }
    }

    public IReadOnlyList<LogLine> Tail(int n)
    {
        lock (_gate)
        {
            var take = Math.Clamp(n, 0, _count);
            var result = new List<LogLine>(take);
            for (var i = _count - take; i < _count; i++)
            {
                result.Add(At(i));
            }
            return result;
        }
    }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate)
        {
            return Collect(_ => true);
        }
    }

    private string Separator(string label) => $"── {label} {_time.GetLocalNow():HH:mm:ss} ──";

    private LogLine AppendCore(LogKind kind, string text, bool isMarker)
    {
        LogLine line;
        Action<LogLine>? handler;
        lock (_gate)
        {
            line = new LogLine(_nextSeq++, _time.GetUtcNow(), kind, text);
            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = line;
                _count++;
            }
            else
            {
                _items[_start] = line;
                _start = (_start + 1) % _items.Length;
            }
            if (isMarker)
            {
                _lastReloadSeq = line.Seq;
            }
            handler = Appended;
        }
        handler?.Invoke(line);
        return line;
    }

    private LogLine At(int index) => _items[(_start + index) % _items.Length]!;

    private List<LogLine> Collect(Func<LogLine, bool> predicate)
    {
        var result = new List<LogLine>();
        for (var i = 0; i < _count; i++)
        {
            var line = At(i);
            if (predicate(line))
            {
                result.Add(line);
            }
        }
        return result;
    }
}
