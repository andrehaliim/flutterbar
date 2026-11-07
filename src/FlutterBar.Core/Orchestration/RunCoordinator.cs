using FlutterBar.Core.Sessions;
using FlutterBar.Core.Settings;

namespace FlutterBar.Core.Orchestration;

/// <summary>
/// ▶ from the widget: captures project/config/device when pressed, runs <see cref="RunOrchestrator"/> under its own
/// cancellation, and saves the result to THAT project. While preparing (device refresh can take seconds) the one-session
/// rule applies too: a selection change calls <see cref="CancelPreparation"/>; a run cancelled after flutter was already
/// launched is stopped again, and nothing is persisted.
/// Call on the UI thread: <paramref name="settings"/> is mutated on the caller's context after the await.
/// </summary>
public sealed class RunCoordinator(RunSession session, RunOrchestrator orchestrator)
{
    private CancellationTokenSource? _preparation;

    public bool IsPreparing => _preparation is not null;

    /// <summary>Cancels an in-flight preparation; false when nothing was being prepared.</summary>
    public bool CancelPreparation()
    {
        if (_preparation is not { } cts)
        {
            return false;
        }
        cts.Cancel();
        return true;
    }

    /// <exception cref="OperationCanceledException"><paramref name="lifetime"/> was cancelled (exit).</exception>
    public async Task<RunOutcome> RunAsync(AppSettings settings, Func<DateTimeOffset> now, CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(now);
        if (_preparation is not null)
        {
            return new RunOutcome(RunOutcomeKind.SessionActive, "Run sedang disiapkan");
        }

        var root = settings.CurrentProject;
        string? config = null;
        string? device = null;
        if (root is not null && settings.TryGetProject(root, out var state))
        {
            config = state.Config;
            device = state.DeviceId;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _preparation = cts;
        RunOutcome outcome;
        try
        {
            outcome = await orchestrator.RunAsync(new RunSelection(root, config, device), cts.Token); // resume on the caller's context
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            return Cancelled(root);
        }
        finally
        {
            _preparation = null;
        }

        if (cts.IsCancellationRequested)
        {
            // Cancelled while flutter was being launched: the user already moved on, so this run must not stay.
            if (outcome.ShouldPersist)
            {
                await session.StopAsync();
            }
            lifetime.ThrowIfCancellationRequested();
            return Cancelled(root);
        }

        if (outcome.ShouldPersist && root is not null && outcome.Config is { } used && outcome.DeviceId is { } deviceId)
        {
            ProjectSelection.RecordRunFor(settings, root, used.Name, deviceId, now());
        }
        return outcome with { ProjectRoot = root };
    }

    private static RunOutcome Cancelled(string? root) =>
        new(RunOutcomeKind.Cancelled, "Run dibatalkan") { ProjectRoot = root };
}
