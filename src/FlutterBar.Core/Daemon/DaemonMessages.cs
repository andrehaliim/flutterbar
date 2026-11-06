namespace FlutterBar.Core.Daemon;

/// <summary>One parsed item of <c>flutter run --machine</c> stdout.</summary>
public abstract record DaemonMessage;

/// <summary>Event <c>app.start</c>.</summary>
public sealed record AppStart(string AppId, string DeviceId, bool SupportsRestart, string LaunchMode) : DaemonMessage;

/// <summary>Event <c>app.started</c>: the app is up and can be reloaded.</summary>
public sealed record AppStarted(string AppId) : DaemonMessage;

/// <summary>Event <c>app.log</c>.</summary>
public sealed record AppLog(string AppId, string Log, bool Error) : DaemonMessage;

/// <summary>Event <c>app.progress</c>; <c>Message</c> is absent on the "finished" event.</summary>
public sealed record AppProgress(string? Message, bool Finished) : DaemonMessage;

/// <summary>Event <c>app.stop</c>.</summary>
public sealed record AppStop(string AppId, string? Error) : DaemonMessage;

/// <summary>Event <c>daemon.logMessage</c>.</summary>
public sealed record DaemonLog(string Level, string Message) : DaemonMessage;

/// <summary>
/// A response to a request. <c>Code</c>/<c>Message</c> come from an object <c>result</c>
/// (e.g. <c>app.restart</c>); both are null when <c>result</c> is a bare value such as <c>true</c> (<c>app.stop</c>).
/// <c>Error</c> is set when the response carries <c>error</c>.
/// </summary>
public sealed record Response(int Id, int? Code, string? Message, string? Error) : DaemonMessage;

/// <summary>Any other event (e.g. <c>daemon.connected</c>, <c>app.debugPort</c>, <c>app.webLaunchUrl</c>).</summary>
public sealed record OtherEvent(string Event) : DaemonMessage;

/// <summary>A line that is not a JSON array of messages (banner, "Launching ...", framework logs, ...).</summary>
public sealed record NonJson(string Text) : DaemonMessage;
