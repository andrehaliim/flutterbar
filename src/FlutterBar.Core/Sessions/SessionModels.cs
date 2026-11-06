using FlutterBar.Core.Config;
using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Sessions;

public enum SessionState
{
    Idle,
    Starting,
    Running,
    Reloading,
    Stopping,
    Failed,
}

public sealed record RunRequest(string ProjectRoot, LaunchConfig Config, string DeviceId, ProcessSpec Spec);

/// <summary>Immutable view of a <see cref="RunSession"/>. <c>HasError</c> is the error flag, independent of the state.</summary>
public sealed record SessionSnapshot(
    SessionState State,
    bool HasError,
    int ErrorCount,
    string? Progress,
    string? FailureMessage,
    bool CanReload,
    bool CanRestart,
    RunRequest? Request);

public enum ReloadFailure
{
    /// <summary>No running app (Idle, Starting without appId, Stopping, Failed).</summary>
    NoSession,

    /// <summary>Another reload/restart is still in flight.</summary>
    Busy,

    /// <summary>Not possible in this session (profile/release, no restart support, reload rejected by flutter).</summary>
    Unsupported,

    /// <summary>Flutter answered with a failure (e.g. compile error) or the session ended while waiting.</summary>
    Failed,

    Timeout,
}

public sealed record ReloadOutcome(bool Success, string Message, ReloadFailure? Failure);
