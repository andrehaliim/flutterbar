using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace StatefulBar.Core.Ipc;

public sealed class IpcClient(string pipeName) : IIpcClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly UTF8Encoding Utf8 = new(false);

    public async Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(ConnectTimeout, ct);
        }
        catch (TimeoutException)
        {
            // Defined contract: no pipe (or none reachable within the connect window) means FlutterBar is not running.
            return null;
        }

        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(ct);
        exchange.CancelAfter(timeout);
        try
        {
            var bytes = Utf8.GetBytes(JsonSerializer.Serialize(request, IpcJson.Options) + "\n");
            await pipe.WriteAsync(bytes, exchange.Token);
            await pipe.FlushAsync(exchange.Token);

            using var reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync(exchange.Token);
            if (line is null)
            {
                return new IpcResponse(false, ExitCodes.Failed, null, "FlutterBar closed the connection without a response");
            }

            var response = JsonSerializer.Deserialize<IpcResponse>(line, IpcJson.Options);
            return response ?? new IpcResponse(false, ExitCodes.Failed, null, "Empty response from FlutterBar");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new IpcResponse(false, ExitCodes.Timeout, null, "Timed out waiting for FlutterBar");
        }
        catch (JsonException ex)
        {
            return new IpcResponse(false, ExitCodes.Failed, null, "Invalid response from FlutterBar: " + ex.Message);
        }
    }
}
