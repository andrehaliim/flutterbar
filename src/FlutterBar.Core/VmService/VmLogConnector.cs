using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using FlutterBar.Core.Diagnostics;

namespace FlutterBar.Core.VmService;

/// <summary>One <c>dart:developer</c> <c>log()</c> call with every part resolved to text.</summary>
public sealed record VmLogRecord(string LoggerName, string Message, int Level, string? Error, string? StackTrace);

/// <summary>Reads <c>log()</c> output from the Dart VM service of a running app.</summary>
public interface IVmLogConnector
{
    /// <summary>
    /// Starts connecting in the background and returns at once. <paramref name="onRecord"/> runs in <c>log()</c> order
    /// on a worker thread; <paramref name="onFailure"/> runs at most once, when the stream could not be opened.
    /// Disposing the result closes the connection, after which neither callback runs again.
    /// </summary>
    IAsyncDisposable Connect(Uri wsUri, Action<VmLogRecord> onRecord, Action<string> onFailure);
}

/// <summary>
/// <see cref="IVmLogConnector"/> over a WebSocket: subscribes to the <c>Logging</c> stream, then per event fetches what
/// the event only references (long strings are cut short, objects such as an <c>Exception</c> carry no text).
/// The URI holds the service's auth token and is never written to diag.log.
/// </summary>
public sealed class VmLogConnector(TimeSpan? connectTimeout = null, TimeSpan? requestTimeout = null) : IVmLogConnector
{
    private readonly TimeSpan _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
    private readonly TimeSpan _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(2);

    public IAsyncDisposable Connect(Uri wsUri, Action<VmLogRecord> onRecord, Action<string> onFailure)
    {
        ArgumentNullException.ThrowIfNull(wsUri);
        ArgumentNullException.ThrowIfNull(onRecord);
        ArgumentNullException.ThrowIfNull(onFailure);
        return new Connection(wsUri, onRecord, onFailure, _connectTimeout, _requestTimeout);
    }

    private sealed class Connection : IAsyncDisposable
    {
        private const string ListenId = "listen";

        private readonly Uri _uri;
        private readonly Action<VmLogRecord> _onRecord;
        private readonly Action<string> _onFailure;
        private readonly TimeSpan _connectTimeout;
        private readonly TimeSpan _requestTimeout;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<VmResponse>> _pending = new();
        private readonly Task _run;
        private int _nextId;
        private int _disposed;

        public Connection(
            Uri uri, Action<VmLogRecord> onRecord, Action<string> onFailure, TimeSpan connectTimeout, TimeSpan requestTimeout)
        {
            _uri = uri;
            _onRecord = onRecord;
            _onFailure = onFailure;
            _connectTimeout = connectTimeout;
            _requestTimeout = requestTimeout;
            _run = Task.Run(RunAsync);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            await _cts.CancelAsync().ConfigureAwait(false);
            await _run.ConfigureAwait(false); // never faults
            _cts.Dispose();
        }

        private async Task RunAsync()
        {
            var ct = _cts.Token;
            using var socket = new ClientWebSocket();
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(_connectTimeout);
                await socket.ConnectAsync(_uri, connectCts.Token).ConfigureAwait(false);
                await SendAsync(socket, VmLogProtocol.EncodeStreamListen(ListenId, VmLogProtocol.LoggingStream), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    // Reported to the user through onFailure and to diag.log; a timeout surfaces as a cancellation.
                    DiagLog.Write("VmLogConnector: couldn't open the VM service log stream", ex);
                    Fail(ex is OperationCanceledException ? "the connection timed out" : ex.Message);
                }
                return;
            }

            // Events are resolved one at a time so records keep their log() order; the receive loop stays free to
            // deliver the answers the resolver waits for.
            var events = Channel.CreateUnbounded<VmLogEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var resolver = ResolveAllAsync(socket, events.Reader, ct);
            try
            {
                await ReceiveAsync(socket, events.Writer, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    // The app usually just went away; the session reports that itself.
                    DiagLog.Write("VmLogConnector: the VM service connection ended", ex);
                }
            }
            finally
            {
                events.Writer.TryComplete();
                foreach (var waiter in _pending.Values)
                {
                    waiter.TrySetCanceled(CancellationToken.None);
                }
                await resolver.ConfigureAwait(false); // never faults
            }
        }

        private async Task ReceiveAsync(ClientWebSocket socket, ChannelWriter<VmLogEvent> events, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                switch (VmLogProtocol.Parse(text))
                {
                    case VmLogEvent logEvent:
                        events.TryWrite(logEvent);
                        break;
                    case VmResponse { Id: ListenId, IsError: true }:
                        DiagLog.Write("VmLogConnector: the VM service rejected streamListen(Logging)");
                        Fail("the VM service rejected the log subscription");
                        break;
                    case VmResponse response when _pending.TryRemove(response.Id, out var waiter):
                        waiter.TrySetResult(response);
                        break;
                    default:
                        // Other streams' events, and answers nobody waits for any more.
                        break;
                }
            }
        }

        private async Task ResolveAllAsync(ClientWebSocket socket, ChannelReader<VmLogEvent> events, CancellationToken ct)
        {
            try
            {
                await foreach (var logEvent in events.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var record = new VmLogRecord(
                        await ResolveAsync(socket, logEvent.IsolateId, logEvent.LoggerName, ct).ConfigureAwait(false) ?? string.Empty,
                        await ResolveAsync(socket, logEvent.IsolateId, logEvent.Message, ct).ConfigureAwait(false) ?? string.Empty,
                        logEvent.Level,
                        await ResolveAsync(socket, logEvent.IsolateId, logEvent.Error, ct).ConfigureAwait(false),
                        await ResolveAsync(socket, logEvent.IsolateId, logEvent.StackTrace, ct).ConfigureAwait(false));
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    _onRecord(record);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Disposed: stop quietly.
            }
            catch (Exception ex)
            {
                // A throwing subscriber or an unexpected failure; log() output stops, the session is unaffected.
                DiagLog.Write("VmLogConnector: reading log records failed", ex);
            }
        }

        /// <summary>The text of <paramref name="value"/>, or null for Dart <c>null</c>. Falls back to what the event carried.</summary>
        private async Task<string?> ResolveAsync(ClientWebSocket socket, string isolateId, VmValue value, CancellationToken ct)
        {
            if (value.IsNull)
            {
                return null;
            }
            if (value.Text is null)
            {
                // A plain object (e.g. an Exception passed as `error`): ask the app for its toString().
                var fallback = value.ClassName is { } name ? $"Instance of '{name}'" : "<object>";
                if (value.ObjectId is null || isolateId.Length == 0)
                {
                    return fallback;
                }
                var answer = await RequestAsync(socket, id => VmLogProtocol.EncodeToString(id, isolateId, value.ObjectId), ct).ConfigureAwait(false);
                if (answer?.Value is not { IsNull: false, Text: not null } text)
                {
                    return fallback;
                }
                value = text;
            }
            if (!value.Truncated)
            {
                return value.Text;
            }

            var cut = value.Text + "…";
            if (value.ObjectId is null || isolateId.Length == 0)
            {
                return cut;
            }
            var full = await RequestAsync(socket, id => VmLogProtocol.EncodeGetObject(id, isolateId, value.ObjectId), ct).ConfigureAwait(false);
            return full?.Value is { Text: { } complete, Truncated: false } ? complete : cut;
        }

        /// <summary>Null when the service answered with an error, did not answer in time, or the connection ended.</summary>
        private async Task<VmResponse?> RequestAsync(ClientWebSocket socket, Func<string, string> encode, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var waiter = new TaskCompletionSource<VmResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = waiter;
            try
            {
                await SendAsync(socket, encode(id), ct).ConfigureAwait(false);
                var response = await waiter.Task.WaitAsync(_requestTimeout, ct).ConfigureAwait(false);
                return response.IsError ? null : response;
            }
            catch (Exception ex) when (ex is TimeoutException or WebSocketException
                || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // The caller falls back to the text the event carried; a dead connection ends the receive loop.
                return null;
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        // Sends never overlap: the subscription is sent before the resolver starts, and the resolver sends one at a time.
        private static Task SendAsync(ClientWebSocket socket, string json, CancellationToken ct) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, ct).AsTask();

        private void Fail(string reason)
        {
            try
            {
                _onFailure(reason);
            }
            catch (Exception ex)
            {
                DiagLog.Write("VmLogConnector: failure subscriber threw", ex);
            }
        }
    }
}
