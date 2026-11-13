using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using StatefulBar.Core.Diagnostics;
using StatefulBar.Core.Ipc;

namespace StatefulBar.Core.Tests.Ipc;

[Collection("DiagLog")]
public sealed class IpcRoundTripTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _diagDir = Path.Combine(Path.GetTempPath(), "FlutterBarPipe-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "FlutterBar-test-" + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _cts = new();
    private Task? _server;

    public IpcRoundTripTests()
    {
        DiagLog.Initialize(Path.Combine(_diagDir, "diag.log"));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _server?.WaitAsync(Wait).GetAwaiter().GetResult();
        _cts.Dispose();
        if (Directory.Exists(_diagDir))
        {
            Directory.Delete(_diagDir, true);
        }
    }

    private void StartServer(Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler)
    {
        _server = new IpcServer(_pipe, handler).RunAsync(_cts.Token);
    }

    private static Task<IpcResponse> Ok(IpcRequest r) =>
        Task.FromResult(new IpcResponse(true, 0, IpcJson.ToElement(new { echo = r.Command }), "hi"));

    [Fact]
    public async Task Client_ServerRoundTrip()
    {
        IpcRequest? seen = null;
        StartServer((r, _) =>
        {
            seen = r;
            return Ok(r);
        });

        var response = await new IpcClient(_pipe).SendAsync(
            new IpcRequest("logs", new Dictionary<string, string> { ["tail"] = "5" }), Wait, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Ok);
        Assert.Equal("hi", response.Message);
        Assert.Equal("logs", response.Data!.Value.GetProperty("echo").GetString());
        Assert.Equal("logs", seen!.Command);
        Assert.Equal("5", seen.Args!["tail"]);
    }

    [Fact]
    public async Task Client_NoServer_ReturnsNullQuickly()
    {
        var sw = Stopwatch.StartNew();

        var response = await new IpcClient(_pipe).SendAsync(new IpcRequest("status", null), Wait, CancellationToken.None);

        Assert.Null(response);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Client_SlowHandler_ReturnsTimeoutExit4()
    {
        StartServer(async (r, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return await Ok(r);
        });

        var response = await new IpcClient(_pipe).SendAsync(new IpcRequest("reload", null), TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(ExitCodes.Timeout, response.ExitCode);
        Assert.Contains("Timed out", response.Message);
    }

    [Fact]
    public async Task Server_SurvivesGarbageAndDisconnectingClients()
    {
        StartServer((r, _) => Ok(r));

        // Garbage line: answered with a Usage error.
        await using (var bad = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await bad.ConnectAsync(2000);
            await bad.WriteAsync(Encoding.UTF8.GetBytes("this is {not json\n"));
            using var reader = new StreamReader(bad, Encoding.UTF8);
            var line = await reader.ReadLineAsync().WaitAsync(Wait);
            var response = JsonSerializer.Deserialize<IpcResponse>(line!, IpcJson.Options);
            Assert.Equal(ExitCodes.Usage, response!.ExitCode);
        }

        // Client that connects and drops without sending anything.
        await using (var silent = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await silent.ConnectAsync(2000);
        }

        // Client that sends a request and vanishes before reading the answer.
        await using (var vanish = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await vanish.ConnectAsync(2000);
            await vanish.WriteAsync(Encoding.UTF8.GetBytes("{\"command\":\"status\"}\n"));
        }

        var good = await new IpcClient(_pipe).SendAsync(new IpcRequest("status", null), Wait, CancellationToken.None);
        Assert.NotNull(good);
        Assert.True(good.Ok);
    }

    [Fact]
    public async Task Server_HandlerThrows_RespondsFailedAndKeepsServing()
    {
        var calls = 0;
        StartServer((r, _) => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("boom") : Ok(r));

        var first = await new IpcClient(_pipe).SendAsync(new IpcRequest("status", null), Wait, CancellationToken.None);
        var second = await new IpcClient(_pipe).SendAsync(new IpcRequest("status", null), Wait, CancellationToken.None);

        Assert.Equal(ExitCodes.Failed, first!.ExitCode);
        Assert.Contains("boom", first.Message);
        Assert.True(second!.Ok);
    }

    [Fact]
    public async Task Server_Cancellation_EndsRunAsyncCleanly()
    {
        StartServer((r, _) => Ok(r));
        Assert.NotNull(await new IpcClient(_pipe).SendAsync(new IpcRequest("status", null), Wait, CancellationToken.None));

        await _cts.CancelAsync();

        await _server!.WaitAsync(Wait);
        Assert.Null(await new IpcClient(_pipe).SendAsync(new IpcRequest("status", null), Wait, CancellationToken.None));
    }

    [Fact]
    public void PipeNames_ForCurrentUser_HasSidSuffix()
    {
        var name = PipeNames.ForCurrentUser();

        Assert.StartsWith("FlutterBar-S-1-", name, StringComparison.Ordinal);
    }
}
