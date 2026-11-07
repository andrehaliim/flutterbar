using System.Globalization;
using FlutterBar.Core.Logging;
using FlutterBar.Core.Sessions;

namespace FlutterBar.Core.Ipc;

/// <summary>
/// Executes IPC commands against the session. reload/restart are not serialized: the session answers a second
/// request immediately with "Sedang reload" (Busy), so queueing it behind the first would make it hang. status and logs
/// only read snapshots, so they are not serialized either (a stop can wait the whole stop grace, longer than the CLI's
/// patience). stop and show run one at a time.
/// </summary>
public sealed class CommandHandler(
    RunSession session,
    LogBuffer log,
    IStatusProvider status,
    Action showWidget,
    TimeSpan? reloadTimeout = null)
{
    private readonly TimeSpan _reloadTimeout = reloadTimeout ?? TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _serial = new(1, 1);

    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (request.Command)
        {
            case "reload":
                return await ReloadAsync(fullRestart: false, ct);
            case "restart":
                return await ReloadAsync(fullRestart: true, ct);
            case "status":
                return Status();
            case "logs":
                return Logs(request.Args);
        }

        await _serial.WaitAsync(ct);
        try
        {
            return request.Command switch
            {
                "stop" => await StopAsync(),
                "show" => Show(),
                _ => Error(ExitCodes.Usage, $"Perintah tidak dikenal: {request.Command}"),
            };
        }
        finally
        {
            _serial.Release();
        }
    }

    private static IpcResponse Error(int exitCode, string message) => new(false, exitCode, null, message);

    private IpcResponse Status()
    {
        var snapshot = session.Snapshot;
        var info = status.Current();
        var data = IpcJson.ToElement(info);
        return snapshot.State switch
        {
            SessionState.Idle => new IpcResponse(false, ExitCodes.NoSession, data, "Tidak ada sesi aktif"),
            SessionState.Failed => new IpcResponse(false, ExitCodes.Failed, data, snapshot.FailureMessage ?? "Sesi gagal"),
            _ => new IpcResponse(true, ExitCodes.Ok, data, string.Empty),
        };
    }

    private async Task<IpcResponse> ReloadAsync(bool fullRestart, CancellationToken ct)
    {
        var outcome = await session.ReloadAsync(fullRestart, _reloadTimeout, ct);
        var data = IpcJson.ToElement(new { success = outcome.Success, message = outcome.Message });
        if (outcome.Success)
        {
            return new IpcResponse(true, ExitCodes.Ok, data, outcome.Message);
        }

        var code = outcome.Failure switch
        {
            ReloadFailure.NoSession => ExitCodes.NoSession,
            ReloadFailure.Timeout => ExitCodes.Timeout,
            _ => ExitCodes.Failed,
        };
        var message = outcome.Failure == ReloadFailure.NoSession && string.IsNullOrWhiteSpace(outcome.Message)
            ? "Tidak ada sesi aktif"
            : outcome.Message;
        return new IpcResponse(false, code, data, message);
    }

    private async Task<IpcResponse> StopAsync()
    {
        if (session.Snapshot.State == SessionState.Idle)
        {
            return Error(ExitCodes.NoSession, "Tidak ada sesi aktif");
        }

        await session.StopAsync();
        return new IpcResponse(true, ExitCodes.Ok, null, "Sesi dihentikan");
    }

    private IpcResponse Show()
    {
        showWidget();
        return new IpcResponse(true, ExitCodes.Ok, null, string.Empty);
    }

    private IpcResponse Logs(IReadOnlyDictionary<string, string>? args)
    {
        var errorsOnly = Flag(args, "errors");
        var sinceLastReload = Flag(args, "sinceLastReload");
        int? tail = null;
        if (args is not null && args.TryGetValue("tail", out var tailText))
        {
            if (!int.TryParse(tailText, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return Error(ExitCodes.Usage, $"Nilai --tail tidak valid: {tailText}");
            }
            tail = n;
        }

        IEnumerable<LogLine> lines = sinceLastReload ? log.SinceLastReload() : log.Snapshot();
        if (errorsOnly)
        {
            lines = lines.Where(l => l.Kind == LogKind.Error);
        }
        var list = lines.ToList();
        if (tail is { } count && count < list.Count)
        {
            list = list.GetRange(list.Count - count, count);
        }

        var data = IpcJson.ToElement(new
        {
            lines = list.Select(l => new { seq = l.Seq, time = l.Timestamp, kind = l.Kind.ToString().ToLowerInvariant(), text = l.Text }),
        });
        return new IpcResponse(true, ExitCodes.Ok, data, string.Empty);
    }

    private static bool Flag(IReadOnlyDictionary<string, string>? args, string key) =>
        args is not null && args.TryGetValue(key, out var v) && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
}
