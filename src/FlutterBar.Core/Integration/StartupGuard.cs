using FlutterBar.Core.Ipc;

namespace FlutterBar.Core.Integration;

public enum UnhandledAction
{
    /// <summary>Log, mark handled, keep the widget alive.</summary>
    KeepRunning,

    /// <summary>Log, release the single-instance mutex and exit with <see cref="StartupGuard.FailedExitCode"/>.</summary>
    ExitProcess,
}

/// <summary>
/// Startup rules around the single-instance mutex. A failure after the mutex is taken but before the widget is up must
/// end the process: otherwise a windowless process keeps the mutex (and no IPC server), and every later launch takes the
/// second-instance path and exits silently.
/// </summary>
public static class StartupGuard
{
    public const int FailedExitCode = ExitCodes.Failed;

    public static UnhandledAction OnUnhandledUiException(bool startupCompleted) =>
        startupCompleted ? UnhandledAction.KeepRunning : UnhandledAction.ExitProcess;

    /// <summary>
    /// Second instance: sends <c>show</c>, retrying while the pipe does not exist yet (the first instance may still be
    /// starting; each connect attempt already waits up to 500 ms) until <paramref name="budget"/> is spent.
    /// Returns the answer (null = never reachable) and the number of attempts.
    /// </summary>
    public static async Task<(IpcResponse? Response, int Attempts)> RequestShowAsync(
        IIpcClient client, TimeSpan budget, TimeSpan retryDelay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var deadline = DateTime.UtcNow + budget;
        var attempts = 0;
        while (true)
        {
            attempts++;
            var response = await client.SendAsync(new IpcRequest("show", null), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            if (response is not null || DateTime.UtcNow + retryDelay >= deadline)
            {
                return (response, attempts);
            }
            await Task.Delay(retryDelay, ct).ConfigureAwait(false);
        }
    }
}
