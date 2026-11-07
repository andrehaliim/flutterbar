using FlutterBar.Core.Config;
using FlutterBar.Core.Devices;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Sessions;

namespace FlutterBar.Core.Orchestration;

/// <summary>What the widget has selected when ▶ is pressed.</summary>
public sealed record RunSelection(string? ProjectRoot, string? ConfigName, string? DeviceId);

public enum RunOutcomeKind
{
    Started,

    /// <summary>The launch itself failed (session ended in Failed); the selection was valid.</summary>
    StartFailed,
    NoProject,
    ProjectMissing,
    NoConfig,
    ConfigInvalid,
    ConfigNotFound,
    NoDevice,
    DeviceMissing,
    DeviceRefreshFailed,
    ToolError,
    SessionActive,

    /// <summary>The selection changed while ▶ was being prepared (<see cref="RunCoordinator.CancelPreparation"/>).</summary>
    Cancelled,
}

/// <param name="Error">The exception behind the failure, for diag.log (device refresh).</param>
public sealed record RunOutcome(RunOutcomeKind Kind, string Message, LaunchConfig? Config = null, string? DeviceId = null, Exception? Error = null)
{
    /// <summary>The selection was valid: LastUsed, config and device are saved to settings.</summary>
    public bool ShouldPersist => Kind is RunOutcomeKind.Started or RunOutcomeKind.StartFailed;

    /// <summary>The project the run was prepared for (captured when ▶ was pressed).</summary>
    public string? ProjectRoot { get; init; }
}

/// <summary>
/// ▶ Run without UI (spec §5.4, brief Step 6): launch.json (re-read) → config → tool resolution → device refresh
/// (missing device aborts) → <see cref="RunSession.StartAsync"/>. The tool is resolved before the device refresh so a
/// missing fvm/flutter reports its own clear message instead of a failed <c>flutter devices</c>. Every continuation
/// runs off the caller's thread (no I/O on the UI thread); the caller applies the outcome.
/// </summary>
public sealed class RunOrchestrator(RunSession session, DeviceService devices, FlutterToolLocator locator)
{
    /// <param name="commit">
    /// Called right before <see cref="RunSession.StartAsync"/>: from then on the run can no longer be cancelled through
    /// <paramref name="ct"/> (a selection change needs the stop confirmation). Returning false aborts (cancelled meanwhile).
    /// </param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<RunOutcome> RunAsync(RunSelection selection, CancellationToken ct, Func<bool>? commit = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(selection.ProjectRoot))
        {
            return new RunOutcome(RunOutcomeKind.NoProject, "Select a project first");
        }
        if (SessionGuard.NeedsStopConfirmation(session.Snapshot.State))
        {
            return new RunOutcome(RunOutcomeKind.SessionActive, "A session is still running");
        }

        var root = selection.ProjectRoot;
        var loaded = await Task.Run(() => Directory.Exists(root) ? LaunchConfigStore.Load(root) : null, ct).ConfigureAwait(false);
        if (loaded is null)
        {
            return new RunOutcome(RunOutcomeKind.ProjectMissing, $"Project folder not found: {root}");
        }
        if (!loaded.FileExists)
        {
            return new RunOutcome(RunOutcomeKind.NoConfig, UiText.NoConfig);
        }
        if (loaded.Errors.Count > 0)
        {
            return new RunOutcome(RunOutcomeKind.ConfigInvalid, $"launch.json has errors ({loaded.Errors.Count})");
        }
        var config = selection.ConfigName is null
            ? loaded.Configs[0]
            : loaded.Configs.FirstOrDefault(c => c.Name == selection.ConfigName);
        if (config is null)
        {
            return new RunOutcome(RunOutcomeKind.ConfigNotFound, ConfigPresentation.NotFoundText(selection.ConfigName!));
        }
        if (string.IsNullOrWhiteSpace(selection.DeviceId))
        {
            return new RunOutcome(RunOutcomeKind.NoDevice, "Select a device first", config);
        }

        var deviceId = selection.DeviceId;
        var args = FlutterCommandBuilder.BuildRunArgs(config, deviceId);
        var tool = locator.Resolve(root, args);
        if (tool.Spec is null)
        {
            return new RunOutcome(RunOutcomeKind.ToolError, tool.Error ?? "flutter can't be run", config, deviceId);
        }

        IReadOnlyList<DeviceInfo> connected;
        try
        {
            connected = await devices.RefreshDevicesAsync(root, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ToolFailure.IsExpected(ex))
        {
            return new RunOutcome(RunOutcomeKind.DeviceRefreshFailed, $"Couldn't read the device list: {ex.Message}", config, deviceId, ex);
        }
        if (!connected.Any(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)))
        {
            return new RunOutcome(RunOutcomeKind.DeviceMissing, $"Device {deviceId} isn't available. Select another device", config, deviceId);
        }

        ct.ThrowIfCancellationRequested();
        if (commit is not null && !commit())
        {
            throw new OperationCanceledException("Run preparation was cancelled", ct);
        }
        var request = new RunRequest(root, config, deviceId, tool.Spec);
        try
        {
            await session.StartAsync(request).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Another start won the race (state checked above): not an error, the running session stays.
            return new RunOutcome(RunOutcomeKind.SessionActive, "A session is still running", config, deviceId);
        }

        var snapshot = session.Snapshot;
        return snapshot.State == SessionState.Failed && ReferenceEquals(snapshot.Request, request)
            ? new RunOutcome(RunOutcomeKind.StartFailed, snapshot.FailureMessage ?? "Couldn't start flutter", config, deviceId)
            : new RunOutcome(RunOutcomeKind.Started, $"Run {config.Name} · {deviceId}", config, deviceId);
    }
}
