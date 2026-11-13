using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StatefulBar.Core.Diagnostics;
using StatefulBar.Core.VmService;

namespace StatefulBar.Core.Tests.VmService;

// Shares the DiagLog collection: failures write to the (static) DiagLog, redirected to a temp file here.
[Collection("DiagLog")]
public sealed class VmLogConnectorTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _diagDir = Path.Combine(Path.GetTempPath(), "FlutterBarVmConn-" + Guid.NewGuid().ToString("N"));

    public VmLogConnectorTests()
    {
        DiagLog.Initialize(Path.Combine(_diagDir, "diag.log"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_diagDir))
            {
                Directory.Delete(_diagDir, true);
            }
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine("test cleanup failed: " + ex.Message);
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static string StringInstance(string id, string value, bool truncated = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "@Instance",
            ["id"] = id,
            ["kind"] = "String",
            ["class"] = new { name = "_OneByteString" },
            ["valueAsString"] = value,
            ["valueAsStringIsTruncated"] = truncated,
        });

    private const string NullInstance = """{"type":"@Instance","kind":"Null","valueAsString":"null"}""";

    private static string LogEvent(string message, string loggerName, int level, string error, string stackTrace) =>
        """{"jsonrpc":"2.0","method":"streamNotify","params":{"streamId":"Logging","event":{"kind":"Logging","isolate":{"id":"isolates/42"},"logRecord":{"level":"""
        + level
        + ",\"message\":" + message
        + ",\"loggerName\":" + loggerName
        + ",\"error\":" + error
        + ",\"stackTrace\":" + stackTrace
        + "}}}}";

    private static string Response(string? id, string body) => "{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\"," + body + "}";

    /// <summary>A scripted VM service: subscribes, sends <paramref name="events"/>, then answers requests via <paramref name="answer"/>.</summary>
    private static async Task ServeAsync(
        HttpListener listener, IReadOnlyList<string> events, Func<JsonElement, string?> answer, List<string> methods, CancellationToken ct)
    {
        var context = await listener.GetContextAsync().WaitAsync(ct);
        var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                using var request = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, received.Count));
                var root = request.RootElement;
                var id = root.GetProperty("id").GetString();
                var method = root.GetProperty("method").GetString()!;
                lock (methods)
                {
                    methods.Add(method);
                }
                if (method == "streamListen")
                {
                    await SendAsync(Response(id, "\"result\":{\"type\":\"Success\"}"));
                    foreach (var logEvent in events)
                    {
                        await SendAsync(logEvent);
                    }
                }
                else if (answer(root) is { } result)
                {
                    await SendAsync(Response(id, "\"result\":" + result));
                }
                else
                {
                    await SendAsync(Response(id, "\"error\":{\"code\":-32000,\"message\":\"nope\"}"));
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The client went away or the test ended.
        }
        finally
        {
            socket.Dispose();
        }

        Task SendAsync(string json) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, ct).AsTask();
    }

    [Fact]
    public async Task Connect_DeliversRecords_ResolvingTruncatedStringsAndErrorObjects()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        using var cts = new CancellationTokenSource(Wait);
        var full = new string('x', 300);
        var methods = new List<string>();
        var events = new[]
        {
            LogEvent(StringInstance("objects/1", "short"), StringInstance("objects/2", "demo"), 0, NullInstance, NullInstance),
            LogEvent(
                StringInstance("objects/3", full[..128], truncated: true),
                StringInstance("objects/4", string.Empty),
                1000,
                """{"type":"@Instance","id":"objects/5","kind":"PlainInstance","class":{"name":"StateError"}}""",
                """{"type":"@Instance","id":"objects/6","kind":"StackTrace","valueAsString":"#0 main"}"""),
            LogEvent(
                StringInstance("objects/7", "third"),
                StringInstance("objects/8", "demo"),
                0,
                """{"type":"@Instance","id":"objects/9","kind":"PlainInstance","class":{"name":"Broken"}}""",
                NullInstance),
        };
        string? Answer(JsonElement request)
        {
            var p = request.GetProperty("params");
            return request.GetProperty("method").GetString() switch
            {
                "getObject" when p.GetProperty("objectId").GetString() == "objects/3" => StringInstance("objects/3", full).Replace("@Instance", "Instance"),
                "invoke" when p.GetProperty("targetId").GetString() == "objects/5" => StringInstance("objects/10", "Bad state: boom"),
                _ => null, // objects/9: toString() fails
            };
        }
        var server = ServeAsync(listener, events, Answer, methods, cts.Token);
        var records = new List<VmLogRecord>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<string>();

        await using (new VmLogConnector().Connect(
            new Uri($"ws://localhost:{port}/token=/ws"),
            record =>
            {
                records.Add(record);
                if (records.Count == events.Length)
                {
                    done.TrySetResult();
                }
            },
            failures.Add))
        {
            await done.Task.WaitAsync(Wait);
        }
        await server.WaitAsync(Wait);

        Assert.Empty(failures);
        Assert.Equal(
            [
                new VmLogRecord("demo", "short", 0, null, null),
                new VmLogRecord(string.Empty, full, 1000, "Bad state: boom", "#0 main"),
                new VmLogRecord("demo", "third", 0, "Instance of 'Broken'", null),
            ],
            records);
        Assert.Equal(["streamListen", "getObject", "invoke", "invoke"], methods);
    }

    [Fact]
    public async Task Connect_NothingListening_ReportsFailureOnce()
    {
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = new List<VmLogRecord>();

        await using var connection = new VmLogConnector().Connect(
            new Uri($"ws://127.0.0.1:{FreePort()}/token=/ws"), records.Add, reason => failure.TrySetResult(reason));
        var reason = await failure.Task.WaitAsync(Wait);

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.DoesNotContain("token=", reason, StringComparison.Ordinal);
        Assert.Empty(records);
        var diag = File.ReadAllText(Path.Combine(_diagDir, "diag.log"));
        Assert.DoesNotContain("token=", diag, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_BeforeAnything_DoesNotReportAFailure()
    {
        var failures = new List<string>();
        var connection = new VmLogConnector().Connect(new Uri($"ws://127.0.0.1:{FreePort()}/ws"), _ => { }, failures.Add);

        await connection.DisposeAsync();
        await connection.DisposeAsync();

        // Either cancelled before connecting or refused; a refusal that won the race is the only allowed report.
        Assert.True(failures.Count <= 1);
    }
}
