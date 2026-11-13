using System.Security.Principal;
using System.Text.Json;

namespace FlutterBar.Core.Ipc;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int NotRunning = 2;
    public const int NoSession = 3;
    public const int Timeout = 4;
    public const int Usage = 64;
}

public sealed record IpcRequest(string Command, IReadOnlyDictionary<string, string>? Args);

public sealed record IpcResponse(bool Ok, int ExitCode, JsonElement? Data, string Message);

public sealed record StatusInfo(string State, string? Project, string? Branch, string? Config, string? Device, int ErrorCount);

/// <summary>Supplies the project/branch/config/device details the session itself does not know (implemented by the app).</summary>
public interface IStatusProvider
{
    StatusInfo Current();
}

public interface IIpcClient
{
    /// <summary>Sends one request. A null result means the FlutterBar pipe does not exist (not running).</summary>
    Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct);
}

public static class PipeNames
{
    public static string ForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Current Windows user has no SID.");
        return "FlutterBar-" + sid;
    }
}

/// <summary>Shared JSON settings of the wire protocol and response payloads (camelCase).</summary>
public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
