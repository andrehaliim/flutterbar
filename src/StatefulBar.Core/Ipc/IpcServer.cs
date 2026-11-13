using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FlutterBar.Core.Diagnostics;

namespace FlutterBar.Core.Ipc;

/// <summary>
/// Named-pipe server: one UTF-8 JSON line request and one JSON line response per connection. Connections are served
/// concurrently; ordering between commands is the handler's concern. A bad or vanishing client never ends the loop.
/// </summary>
public sealed class IpcServer(string pipeName, Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler)
{
    private const int MaxRequestChars = 1_000_000;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding Utf8 = new(false);

    public async Task RunAsync(CancellationToken ct)
    {
        var connections = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var server = CreateServer();
                try
                {
                    await server.WaitForConnectionAsync(ct);
                }
                catch (IOException ex)
                {
                    await server.DisposeAsync();
                    DiagLog.Write("IPC accept failed", ex);
                    await Task.Delay(100, ct);
                    continue;
                }
                catch
                {
                    await server.DisposeAsync();
                    throw;
                }

                connections.RemoveAll(t => t.IsCompleted);
                connections.Add(ServeAsync(server, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        await Task.WhenAll(connections);
    }

    private NamedPipeServerStream CreateServer()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Current Windows user has no SID.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, // CurrentUserOnly cannot be combined with an explicit PipeSecurity; the ACL restricts to the current user.
            0,
            0,
            security);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        try
        {
            using var reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
            IpcResponse response;
            try
            {
                response = await ReadAndHandleAsync(reader, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DiagLog.Write("IPC handler failed", ex);
                response = new IpcResponse(false, ExitCodes.Failed, null, "Internal error: " + ex.Message);
            }

            var bytes = Utf8.GetBytes(JsonSerializer.Serialize(response, IpcJson.Options) + "\n");
            await pipe.WriteAsync(bytes, ct);
            await pipe.FlushAsync(ct);
        }
        catch (IOException ex)
        {
            DiagLog.Write("IPC client disconnected before the response was written", ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown while writing.
        }
    }

    private async Task<IpcResponse> ReadAndHandleAsync(StreamReader reader, CancellationToken ct)
    {
        string? line;
        using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            readCts.CancelAfter(RequestReadTimeout);
            try
            {
                line = await reader.ReadLineAsync(readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new IpcResponse(false, ExitCodes.Usage, null, "Request wasn't received in time");
            }
        }

        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxRequestChars)
        {
            return new IpcResponse(false, ExitCodes.Usage, null, "Request is empty or invalid");
        }

        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcRequest>(line, IpcJson.Options);
        }
        catch (JsonException ex)
        {
            DiagLog.Write("IPC received malformed request", ex);
            return new IpcResponse(false, ExitCodes.Usage, null, "Request isn't valid JSON");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Command))
        {
            return new IpcResponse(false, ExitCodes.Usage, null, "Request has no command");
        }

        return await handler(request, ct);
    }
}
