using FlutterBar.Core.VmService;

namespace FlutterBar.Core.Tests.Fakes;

/// <summary>Records connections and lets a test push <c>log()</c> records or a failure into the newest one.</summary>
public sealed class FakeVmLogConnector : IVmLogConnector
{
    private readonly object _gate = new();
    private readonly List<FakeConnection> _connections = [];

    /// <summary>When set, <see cref="Connect"/> throws it.</summary>
    public Exception? ConnectException { get; set; }

    public IReadOnlyList<FakeConnection> Connections
    {
        get
        {
            lock (_gate)
            {
                return [.. _connections];
            }
        }
    }

    public IAsyncDisposable Connect(Uri wsUri, Action<VmLogRecord> onRecord, Action<string> onFailure)
    {
        if (ConnectException is { } ex)
        {
            throw ex;
        }
        var connection = new FakeConnection(wsUri, onRecord, onFailure);
        lock (_gate)
        {
            _connections.Add(connection);
        }
        return connection;
    }

    public sealed class FakeConnection(Uri uri, Action<VmLogRecord> onRecord, Action<string> onFailure) : IAsyncDisposable
    {
        private int _disposeCount;

        public Uri Uri { get; } = uri;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Emit(VmLogRecord record) => onRecord(record);

        public void Fail(string reason) => onFailure(reason);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
