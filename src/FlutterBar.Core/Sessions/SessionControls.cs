using FlutterBar.Core.Config;

namespace FlutterBar.Core.Sessions;

public enum StatusDot
{
    /// <summary>Grey.</summary>
    Idle,

    /// <summary>Blinking yellow.</summary>
    Starting,

    /// <summary>Green.</summary>
    Running,

    /// <summary>Blinking grey.</summary>
    Stopping,

    /// <summary>Red: Failed, or the error flag is set.</summary>
    Error,
}

/// <summary>What the widget shows for a <see cref="SessionSnapshot"/> (spec §6.2 table). Pure, so the UI stays thin.</summary>
public sealed record SessionControls(
    StatusDot Dot,
    string StatusText,
    bool ShowProgress,
    bool ShowRun,
    bool ShowReload,
    bool ReloadEnabled,
    string ReloadToolTip,
    bool ShowRestart,
    bool RestartEnabled,
    string RestartToolTip,
    bool ShowStop,
    bool StopEnabled)
{
    public const string UnsupportedModeText = "Not supported in release/profile mode";
    public const string ReloadUnsupportedText = "Hot reload isn't supported on this device";
    public const string RestartUnsupportedText = "Hot restart isn't supported on this device";
    public const string BusyText = "Reload in progress";

    public static SessionControls From(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var releaseLike = snapshot.Request?.Config.Mode is RunMode.Profile or RunMode.Release;
        var errorSuffix = snapshot.ErrorCount > 0 ? $" · {snapshot.ErrorCount} error" : snapshot.HasError ? " · has errors" : string.Empty;

        switch (snapshot.State)
        {
            case SessionState.Starting:
                return Buttons(StatusDot.Starting, snapshot.Progress ?? "Memulai…", progress: true, run: false, reload: false, stop: true);

            case SessionState.Running:
                {
                    var c = Buttons(snapshot.HasError ? StatusDot.Error : StatusDot.Running, "Running" + errorSuffix, progress: false, run: false, reload: true, stop: true);
                    return c with
                    {
                        ReloadEnabled = snapshot.CanReload,
                        ReloadToolTip = snapshot.CanReload ? "Hot reload" : releaseLike ? UnsupportedModeText : ReloadUnsupportedText,
                        RestartEnabled = snapshot.CanRestart,
                        RestartToolTip = snapshot.CanRestart ? "Hot restart" : releaseLike ? UnsupportedModeText : RestartUnsupportedText,
                    };
                }

            case SessionState.Reloading:
                {
                    var c = Buttons(snapshot.HasError ? StatusDot.Error : StatusDot.Running, BusyText + errorSuffix, progress: false, run: false, reload: true, stop: true);
                    return c with { ReloadToolTip = BusyText, RestartToolTip = BusyText };
                }

            case SessionState.Stopping:
                return Buttons(StatusDot.Stopping, "Menghentikan…", progress: false, run: false, reload: false, stop: false);

            case SessionState.Failed:
                return Buttons(StatusDot.Error, snapshot.FailureMessage ?? "App stopped", progress: false, run: true, reload: false, stop: false);

            default:
                return Buttons(snapshot.HasError ? StatusDot.Error : StatusDot.Idle, "No session" + errorSuffix, progress: false, run: true, reload: false, stop: false);
        }
    }

    private static SessionControls Buttons(StatusDot dot, string text, bool progress, bool run, bool reload, bool stop) =>
        new(dot, text, progress, run, reload, false, "Hot reload", reload, false, "Hot restart", stop, stop);
}
