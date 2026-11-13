using StatefulBar.Core.Settings;

namespace StatefulBar.Core.Orchestration;

/// <summary>What <see cref="RunCoordinator.CancelPreparation"/> did.</summary>
public enum PreparationCancel
{
    /// <summary>No ▶ in flight: apply the normal one-session rule.</summary>
    None,

    /// <summary>The run was still being prepared and is cancelled; no flutter process was started.</summary>
    Cancelled,

    /// <summary>
    /// flutter is already being launched: nothing was cancelled. Wait for <see cref="RunCoordinator.WaitForLaunchAsync"/>,
    /// then ask "Stop the current session and switch?" like for any running session.
    /// </summary>
    Launching,
}

/// <summary>
/// ▶ from the widget: captures project/config/device when pressed, runs <see cref="RunOrchestrator"/> under its own
/// cancellation and saves the result to THAT project. While the run is being prepared (device refresh, tool resolution)
/// a selection change cancels it (<see cref="CancelPreparation"/>). Right before <c>RunSession.StartAsync</c> the run is
/// committed (under a lock, so cancel and commit never both win); after that the coordinator never stops the session:
/// the stop confirmation decides. Call on the UI thread: <paramref name="settings"/> is mutated after the await.
/// </summary>
public sealed class RunCoordinator(RunOrchestrator orchestrator)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _preparation;
    private bool _committed;
    private TaskCompletionSource? _launch;

    public bool IsPreparing
    {
        get
        {
            lock (_gate)
            {
                return _preparation is not null;
            }
        }
    }

    public PreparationCancel CancelPreparation()
    {
        lock (_gate)
        {
            if (_preparation is not { } cts || cts.IsCancellationRequested)
            {
                return PreparationCancel.None;
            }
            if (_committed)
            {
                return PreparationCancel.Launching;
            }
            cts.Cancel();
            return PreparationCancel.Cancelled;
        }
    }

    /// <summary>Completes once the in-flight ▶ finished (flutter launched, failed or cancelled); completed when idle.</summary>
    public Task WaitForLaunchAsync()
    {
        lock (_gate)
        {
            return _launch?.Task ?? Task.CompletedTask;
        }
    }

    /// <exception cref="OperationCanceledException"><paramref name="lifetime"/> was cancelled (exit).</exception>
    public async Task<RunOutcome> RunAsync(AppSettings settings, Func<DateTimeOffset> now, CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(now);
        var root = settings.CurrentProject;
        string? config = null;
        string? device = null;
        if (root is not null && settings.TryGetProject(root, out var state))
        {
            config = state.Config;
            device = state.DeviceId;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_preparation is not null)
            {
                return new RunOutcome(RunOutcomeKind.SessionActive, "Run is being prepared") { ProjectRoot = root };
            }
            _preparation = cts;
            _committed = false;
            _launch = launch;
        }

        RunOutcome outcome;
        try
        {
            outcome = await orchestrator.RunAsync(new RunSelection(root, config, device), cts.Token, Commit); // resume on the caller's context
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            return Cancelled(root);
        }
        finally
        {
            lock (_gate)
            {
                _preparation = null;
                _committed = false;
                _launch = null;
            }
            launch.TrySetResult();
        }

        if (cts.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            return Cancelled(root); // cancelled during an early return (no process was started)
        }
        if (outcome.ShouldPersist && root is not null && outcome.Config is { } used && outcome.DeviceId is { } deviceId)
        {
            ProjectSelection.RecordRunFor(settings, root, used.Name, deviceId, now());
        }
        return outcome with { ProjectRoot = root };

        bool Commit()
        {
            lock (_gate)
            {
                if (cts.IsCancellationRequested)
                {
                    return false;
                }
                _committed = true;
                return true;
            }
        }
    }

    private static RunOutcome Cancelled(string? root) =>
        new(RunOutcomeKind.Cancelled, "Run cancelled") { ProjectRoot = root };
}
