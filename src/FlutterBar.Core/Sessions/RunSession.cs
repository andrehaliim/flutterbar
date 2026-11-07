using FlutterBar.Core.Config;
using FlutterBar.Core.Daemon;
using FlutterBar.Core.Diagnostics;
using FlutterBar.Core.Logging;
using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Sessions;

/// <summary>
/// One <c>flutter run --machine</c> session (spec §6.2). Transitions:
/// Idle/Failed --Start--> Starting --app.started--> Running &lt;--&gt; Reloading;
/// Starting --exit / app.stop with error--> Failed; Running/Reloading --unexpected exit / app.stop with error--> Failed;
/// any active state --Stop--> Stopping --> Idle.
/// <para>
/// <paramref name="retainedJobs"/> keeps the jobs of processes that exited on their own; when null the session owns one
/// and closes it in <see cref="DisposeAsync"/> (FlutterBar exit).
/// </para>
/// <para>
/// State changes driven by process output happen on the reader threads in stream order; <see cref="Changed"/> is raised
/// outside the internal lock on whichever thread caused the change. Notifications from different threads may arrive out
/// of order, so <see cref="Snapshot"/> is the authoritative latest state.
/// </para>
/// </summary>
public sealed class RunSession(
    IProcessLauncher launcher, LogBuffer log, TimeSpan? stopGrace = null, RetainedJobs? retainedJobs = null) : IAsyncDisposable
{
    private const string BusyMessage = "Reload in progress";

    private static readonly SessionSnapshot IdleSnapshot = new(SessionState.Idle, false, 0, null, null, false, false, null);

    private readonly IProcessLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    private readonly LogBuffer _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly TimeSpan _stopGrace = stopGrace ?? TimeSpan.FromSeconds(5);
    private readonly bool _ownsRetainedJobs = retainedJobs is null;
    private readonly RetainedJobs _retainedJobs = retainedJobs ?? new RetainedJobs();
    private readonly object _gate = new();

    private SessionState _state = SessionState.Idle;
    private Context? _ctx;
    private bool _hasError;
    private int _errorCount;
    private string? _progress;
    private string? _failure;
    private SessionSnapshot _snapshot = IdleSnapshot;
    private Task? _stopTask;
    private bool _disposed;
    private int _nextId;

    /// <summary>
    /// Raised after every state change, on any thread (UI must marshal), outside the internal lock. A throwing handler
    /// is written to diag.log and does not affect the session.
    /// </summary>
    public event Action<SessionSnapshot>? Changed;

    public SessionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>
    /// Launches flutter. Completes once the process was started (or the launch failed, which ends in Failed);
    /// the transition to Running follows <c>app.started</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The state is not Idle or Failed.</exception>
    public async Task StartAsync(RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Context ctx;
        IProcessHandle? previous;
        SessionSnapshot? changed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is not (SessionState.Idle or SessionState.Failed))
            {
                throw new InvalidOperationException($"Cannot start a session while {_state}.");
            }
            previous = _ctx?.Handle;
            ctx = new Context(request);
            _ctx = ctx;
            _state = SessionState.Starting;
            _hasError = false;
            _errorCount = 0;
            _progress = null;
            _failure = null;
            changed = CommitLocked();
        }
        Raise(changed);

        IProcessHandle handle;
        try
        {
            if (previous is not null)
            {
                await ShutdownAsync(previous).ConfigureAwait(false);
            }
            _log.MarkSessionStart($"Run {request.Config.Name} · {request.DeviceId}");
            handle = _launcher.Start(request.Spec);
        }
        catch (Exception ex)
        {
            // Reported to the user through FailureMessage and the log, and to diag.log.
            DiagLog.Write($"RunSession: failed to launch {request.Spec.FileName}", ex);
            var message = $"Couldn't start flutter: {ex.Message}";
            lock (_gate)
            {
                changed = null;
                if (_ctx == ctx && _state == SessionState.Starting)
                {
                    FailLocked(message);
                    changed = CommitLocked();
                }
            }
            Raise(changed);
            _log.Append(LogKind.Error, message);
            return;
        }

        bool abandoned;
        lock (_gate)
        {
            abandoned = _ctx != ctx || ctx.StopRequested;
            if (!abandoned)
            {
                ctx.Handle = handle;
            }
        }
        if (abandoned)
        {
            // StopAsync ran while the process was being launched.
            await ShutdownAsync(handle).ConfigureAwait(false);
            return;
        }

        // Subscribing replays buffered lines synchronously on this thread.
        handle.StdoutLine += line => OnStdout(ctx, line);
        handle.StderrLine += line => OnStderr(ctx, line);
        _ = ObserveExitAsync(ctx, handle);
    }

    /// <summary>
    /// Hot reload (<paramref name="fullRestart"/> false) or hot restart. Never hangs: answers NoSession/Busy/Unsupported
    /// immediately, Timeout after <paramref name="timeout"/>, Failed as soon as the process exits.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled (the session returns to Running).</exception>
    public async Task<ReloadOutcome> ReloadAsync(bool fullRestart, TimeSpan timeout, CancellationToken ct = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be non-negative or infinite.");
        }
        ct.ThrowIfCancellationRequested();
        var label = fullRestart ? "Hot restart" : "Hot reload";
        Context ctx;
        PendingReload pending;
        IProcessHandle handle;
        string appId;
        SessionSnapshot? changed;
        lock (_gate)
        {
            if (_state == SessionState.Reloading)
            {
                return new ReloadOutcome(false, BusyMessage, ReloadFailure.Busy);
            }
            if (_state != SessionState.Running || _ctx is not { AppId: { } id, Handle: { } h } c)
            {
                return new ReloadOutcome(false, "No session running", ReloadFailure.NoSession);
            }
            if (c.Request.Config.Mode != RunMode.Debug)
            {
                return new ReloadOutcome(false, "Not supported in release/profile mode", ReloadFailure.Unsupported);
            }
            if (fullRestart && !c.SupportsRestart)
            {
                return new ReloadOutcome(false, "Hot restart isn't supported for this session", ReloadFailure.Unsupported);
            }
            if (!fullRestart && c.ReloadRejection is { } rejection)
            {
                return new ReloadOutcome(false, rejection, ReloadFailure.Unsupported);
            }

            ctx = c;
            handle = h;
            appId = id;
            // A block whose footer never came (output cut off) must not swallow the output after this reload.
            ctx.StdoutInException = false;
            ctx.StderrInException = false;
            pending = new PendingReload(Interlocked.Increment(ref _nextId), fullRestart);
            ctx.Pending = pending;
            _state = SessionState.Reloading;
            changed = CommitLocked();
        }
        Raise(changed);
        try
        {
            _log.MarkReload(label);
        }
        catch
        {
            // Restore Running so the session is not stuck in Reloading, then let the caller see the error.
            Abandon(ctx, pending);
            throw;
        }

        try
        {
            await handle.WriteLineAsync(DaemonProtocol.EncodeRestart(pending.Id, appId, fullRestart)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Reported via the outcome, the log and diag.log (IOException / closed stdin when the process is dying).
            DiagLog.Write("RunSession: failed to send app.restart", ex);
            var failed = new ReloadOutcome(false, $"Couldn't send the command to flutter: {ex.Message}", ReloadFailure.Failed);
            if (Abandon(ctx, pending))
            {
                _log.Append(LogKind.Error, failed.Message);
                return failed;
            }
            return await pending.Completion.Task.ConfigureAwait(false);
        }

        try
        {
            return await pending.Completion.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!Abandon(ctx, pending))
            {
                // The answer (or the exit) won the race; it is being completed right now.
                return await pending.Completion.Task.ConfigureAwait(false);
            }
            var message = $"{label} timed out: flutter didn't respond within {timeout.TotalSeconds:0.#} seconds";
            _log.Append(LogKind.Error, message);
            return new ReloadOutcome(false, message, ReloadFailure.Timeout);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!Abandon(ctx, pending))
            {
                return await pending.Completion.Task.ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>
    /// Sends <c>app.stop</c> when an app is running and waits up to the stop grace for the exit, otherwise (or when
    /// still Starting) kills the process tree. A process that exited keeps its job in the <see cref="RetainedJobs"/>
    /// (warm Gradle/adb daemons until FlutterBar exits). Always disposes the process handle and ends Idle.
    /// </summary>
    public Task StopAsync()
    {
        Context ctx;
        PendingReload? pending;
        bool sendStop;
        TaskCompletionSource done;
        SessionSnapshot? changed;
        lock (_gate)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }
            if (_ctx is null)
            {
                return Task.CompletedTask;
            }
            ctx = _ctx;
            sendStop = _state is SessionState.Running or SessionState.Reloading && ctx.AppId is not null;
            ctx.StopRequested = true;
            pending = ctx.Pending;
            ctx.Pending = null;
            _state = SessionState.Stopping;
            _progress = null;
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = done.Task;
            changed = CommitLocked();
        }
        Raise(changed);
        pending?.Completion.TrySetResult(new ReloadOutcome(false, "Session stopped", ReloadFailure.Failed));
        _ = RunStopAsync(ctx, sendStop, done); // never faults: failures complete `done` instead
        return done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_ownsRetainedJobs)
            {
                _retainedJobs.Dispose();
            }
        }
    }

    private async Task RunStopAsync(Context ctx, bool sendStop, TaskCompletionSource done)
    {
        Exception? failure = null;
        try
        {
            if (ctx.Handle is { } handle)
            {
                await StopProcessAsync(handle, sendStop ? ctx.AppId : null).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Surfaced to the StopAsync caller below; the session still ends Idle.
            DiagLog.Write("RunSession: stop failed", ex);
            failure = ex;
        }

        SessionSnapshot? changed;
        lock (_gate)
        {
            if (_ctx == ctx)
            {
                _ctx = null;
                _state = SessionState.Idle;
                _hasError = false;
                _errorCount = 0;
                _progress = null;
                _failure = null;
            }
            _stopTask = null;
            changed = CommitLocked();
        }
        Raise(changed);
        if (failure is null)
        {
            done.TrySetResult();
        }
        else
        {
            done.TrySetException(failure);
        }
    }

    /// <summary>
    /// Spec §6.2: <c>app.stop</c>, then the job is closed only when flutter has not exited within the stop grace. When
    /// it exited (now or earlier), its job is retained instead, so daemons it started (Gradle, adb) stay warm until
    /// FlutterBar exits.
    /// </summary>
    private async Task StopProcessAsync(IProcessHandle handle, string? appId)
    {
        var exited = handle.Exited.IsCompleted;
        if (!exited && appId is not null)
        {
            try
            {
                await handle.WriteLineAsync(DaemonProtocol.EncodeStop(Interlocked.Increment(ref _nextId), appId)).ConfigureAwait(false);
                exited = await Task.WhenAny(handle.Exited, Task.Delay(_stopGrace)).ConfigureAwait(false) == handle.Exited;
            }
            catch (Exception ex)
            {
                // Falls through to KillTree; the process must not outlive the session.
                DiagLog.Write("RunSession: failed to send app.stop, killing the process tree", ex);
            }
        }
        await ReleaseAsync(handle, exited).ConfigureAwait(false);
    }

    /// <summary>Ends a previous (Failed) or abandoned process: kept job if it exited by itself, else the tree is killed.</summary>
    private Task ShutdownAsync(IProcessHandle handle) => ReleaseAsync(handle, handle.Exited.IsCompleted);

    private async Task ReleaseAsync(IProcessHandle handle, bool exited)
    {
        if (exited)
        {
            if (handle.DetachJob() is { } job)
            {
                _retainedJobs.Retain(job);
            }
        }
        else
        {
            handle.KillTree();
        }
        await handle.DisposeAsync().ConfigureAwait(false);
    }

    private bool Abandon(Context ctx, PendingReload pending)
    {
        SessionSnapshot? changed;
        lock (_gate)
        {
            if (ctx.Pending != pending)
            {
                return false;
            }
            ctx.Pending = null;
            if (_ctx == ctx && _state == SessionState.Reloading)
            {
                _state = SessionState.Running;
            }
            changed = CommitLocked();
        }
        Raise(changed);
        return true;
    }

    // ------------------------------------------------------------------ process output

    private void OnStdout(Context ctx, string line)
    {
        try
        {
            HandleStdout(ctx, line);
        }
        catch (Exception ex)
        {
            HandlerFailed(ctx, "stdout", ex);
        }
    }

    private void OnStderr(Context ctx, string line)
    {
        try
        {
            HandleStderr(ctx, line);
        }
        catch (Exception ex)
        {
            HandlerFailed(ctx, "stderr", ex);
        }
    }

    private void HandleStdout(Context ctx, string line)
    {
        var messages = DaemonProtocol.ParseLine(line);
        var appends = new List<(LogKind Kind, string Text)>();
        (PendingReload Pending, ReloadOutcome Outcome)? completion = null;
        SessionSnapshot? changed;
        lock (_gate)
        {
            if (_ctx != ctx)
            {
                return;
            }
            foreach (var message in messages)
            {
                switch (message)
                {
                    case AppStart start:
                        if (start.AppId.Length > 0)
                        {
                            ctx.AppId = start.AppId;
                        }
                        ctx.SupportsRestart = start.SupportsRestart;
                        break;
                    case AppStarted started:
                        if (started.AppId.Length > 0)
                        {
                            ctx.AppId = started.AppId;
                        }
                        if (_state == SessionState.Starting && ctx.AppId is not null)
                        {
                            _state = SessionState.Running;
                            _progress = null;
                        }
                        break;
                    case AppProgress progress:
                        if (progress.Finished)
                        {
                            _progress = null;
                        }
                        else if (progress.Message is { } text)
                        {
                            _progress = text;
                            appends.Add((LogKind.Tool, text));
                        }
                        break;
                    case AppLog appLog:
                        foreach (var text in SplitLines(appLog.Log))
                        {
                            var kind = ClassifyLocked(ref ctx.StdoutInException, text, LogKind.App);
                            if (appLog.Error)
                            {
                                kind = LogKind.Error;
                                _hasError = true;
                            }
                            appends.Add((kind, text));
                        }
                        break;
                    case DaemonLog daemonLog:
                        var isError = string.Equals(daemonLog.Level, "error", StringComparison.OrdinalIgnoreCase);
                        foreach (var text in SplitLines(daemonLog.Message))
                        {
                            var kind = ClassifyLocked(ref ctx.StdoutInException, text, LogKind.Tool);
                            if (isError)
                            {
                                kind = LogKind.Error;
                                _hasError = true;
                            }
                            appends.Add((kind, text));
                        }
                        break;
                    case AppStop stop when stop.Error is { } error:
                        _hasError = true;
                        appends.Add((LogKind.Error, error));
                        if (!ctx.StopRequested && _state is SessionState.Starting or SessionState.Running or SessionState.Reloading)
                        {
                            var failure = _state == SessionState.Starting ? $"Build failed: {error}" : $"App stopped: {error}";
                            FailLocked(failure);
                            if (ctx.Pending is { } interrupted)
                            {
                                ctx.Pending = null;
                                completion = (interrupted, new ReloadOutcome(false, failure, ReloadFailure.Failed));
                            }
                        }
                        break;
                    case Response response when ctx.Pending is { } pending && pending.Id == response.Id:
                        ctx.Pending = null;
                        completion = (pending, EvaluateLocked(ctx, pending, response, appends));
                        break;
                    case NonJson nonJson:
                        appends.Add((ClassifyLocked(ref ctx.StdoutInException, nonJson.Text, LogKind.Tool), nonJson.Text));
                        break;
                    default:
                        // OtherEvent, app.stop without error (the exit decides), responses nobody waits for.
                        break;
                }
            }
            changed = CommitLocked();
        }

        try
        {
            foreach (var (kind, text) in appends)
            {
                _log.Append(kind, text);
            }
        }
        finally
        {
            // Even if a log subscriber threw: publish the state and never leave a reload waiting.
            Raise(changed);
            if (completion is { } c)
            {
                c.Pending.Completion.TrySetResult(c.Outcome);
            }
        }
    }

    private void HandleStderr(Context ctx, string line)
    {
        LogKind kind;
        SessionSnapshot? changed;
        lock (_gate)
        {
            if (_ctx != ctx)
            {
                return;
            }
            kind = ClassifyLocked(ref ctx.StderrInException, line, LogKind.Error);
            if (!string.IsNullOrWhiteSpace(line))
            {
                ctx.LastStderr = line.Trim();
            }
            changed = CommitLocked();
        }
        try
        {
            _log.Append(kind, line);
        }
        finally
        {
            Raise(changed);
        }
    }

    /// <summary>Applies a reload/restart response (spec §6.1/§6.2): rejection, failure, or success.</summary>
    private ReloadOutcome EvaluateLocked(Context ctx, PendingReload pending, Response response, List<(LogKind Kind, string Text)> appends)
    {
        var label = pending.FullRestart ? "Hot restart" : "Hot reload";
        if (_state == SessionState.Reloading)
        {
            _state = SessionState.Running;
        }

        if (response.Error is not null || IsUnsupported(response.Message))
        {
            var flutterMessage = response.Error ?? response.Message ?? string.Empty;
            var message = $"{label} rejected by flutter: {flutterMessage}";
            if (!pending.FullRestart)
            {
                ctx.ReloadRejection = message;
            }
            _hasError = true;
            appends.Add((LogKind.Error, message));
            return new ReloadOutcome(false, message, ReloadFailure.Unsupported);
        }

        if (response.Code is int code && code != 0)
        {
            _hasError = true;
            var summary = string.IsNullOrWhiteSpace(response.Message) ? $"{label} failed (code {code})" : response.Message;
            // Compiler diagnostics arrive on stderr before the response; they are already in the log after the marker.
            var details = _log.SinceLastReload()
                .Where(l => l.Kind == LogKind.Error)
                .Select(l => l.Text);
            appends.Add((LogKind.Error, summary));
            return new ReloadOutcome(false, string.Join(Environment.NewLine, [summary, .. details]), ReloadFailure.Failed);
        }

        _hasError = false;
        _errorCount = 0;
        var text = string.IsNullOrWhiteSpace(response.Message) ? $"{label} finished" : response.Message;
        appends.Add((LogKind.Reload, text));
        return new ReloadOutcome(true, text, null);
    }

    private static bool IsUnsupported(string? message) =>
        message is not null
        && (message.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("isn't supported", StringComparison.OrdinalIgnoreCase));

    /// <summary>Exception-block tracking: header through footer are errors; each header counts once.</summary>
    private LogKind ClassifyLocked(ref bool inException, string text, LogKind normal)
    {
        if (LogClassifier.IsExceptionHeader(text))
        {
            inException = true;
            _errorCount++;
            _hasError = true;
            return LogKind.Error;
        }
        if (inException)
        {
            if (LogClassifier.IsExceptionFooter(text))
            {
                inException = false;
            }
            return LogKind.Error;
        }
        return normal;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r'));

    private async Task ObserveExitAsync(Context ctx, IProcessHandle handle)
    {
        try
        {
            var code = await handle.Exited.ConfigureAwait(false);
            OnExited(ctx, code);
        }
        catch (Exception ex)
        {
            HandlerFailed(ctx, "exit", ex);
        }
    }

    private void OnExited(Context ctx, int code)
    {
        PendingReload? pending;
        string? failure = null;
        SessionSnapshot? changed;
        lock (_gate)
        {
            if (_ctx != ctx)
            {
                return;
            }
            pending = ctx.Pending;
            ctx.Pending = null;
            if (!ctx.StopRequested)
            {
                var suffix = ctx.LastStderr is { } last ? $": {last}" : string.Empty;
                if (_state == SessionState.Starting)
                {
                    failure = $"Build failed (exit code {code}){suffix}";
                }
                else if (_state is SessionState.Running or SessionState.Reloading)
                {
                    failure = $"App stopped (exit code {code}){suffix}";
                }
                if (failure is not null)
                {
                    FailLocked(failure);
                }
            }
            changed = CommitLocked();
        }

        pending?.Completion.TrySetResult(new ReloadOutcome(
            false, failure ?? $"flutter process stopped (exit code {code})", ReloadFailure.Failed));
        Raise(changed);
        if (failure is not null)
        {
            _log.Append(LogKind.Error, failure);
        }
    }

    /// <summary>A handler threw: report to diag.log, fail the session, kill the process. Must not throw itself.</summary>
    private void HandlerFailed(Context ctx, string source, Exception ex)
    {
        DiagLog.Write($"RunSession: {source} handler failed", ex);
        var message = $"FlutterBar internal error: {ex.Message}";
        PendingReload? pending;
        SessionSnapshot? changed;
        var kill = false;
        lock (_gate)
        {
            if (_ctx != ctx)
            {
                return;
            }
            pending = ctx.Pending;
            ctx.Pending = null;
            if (_state is SessionState.Starting or SessionState.Running or SessionState.Reloading)
            {
                FailLocked(message);
                kill = true;
            }
            changed = CommitLocked();
        }
        pending?.Completion.TrySetResult(new ReloadOutcome(false, message, ReloadFailure.Failed));
        Raise(changed);
        if (kill)
        {
            try
            {
                ctx.Handle?.KillTree();
            }
            catch (Exception secondary)
            {
                // Last resort on a reader thread: the launcher does not catch, so this must not escape.
                DiagLog.Write("RunSession: KillTree after handler failure failed", secondary);
            }
        }
    }

    // ------------------------------------------------------------------ state

    private void FailLocked(string message)
    {
        _state = SessionState.Failed;
        _failure = message;
        _progress = null;
    }

    /// <summary>Rebuilds the snapshot; returns it when it changed, else null.</summary>
    private SessionSnapshot? CommitLocked()
    {
        var running = _state == SessionState.Running;
        var debug = _ctx?.Request.Config.Mode == RunMode.Debug;
        var snapshot = new SessionSnapshot(
            _state,
            _hasError,
            _errorCount,
            _progress,
            _state == SessionState.Failed ? _failure : null,
            running && debug && _ctx?.ReloadRejection is null,
            running && debug && _ctx?.SupportsRestart == true,
            _ctx?.Request);
        if (snapshot == _snapshot)
        {
            return null;
        }
        _snapshot = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Notifies each subscriber; a throwing subscriber is reported to diag.log and does not affect the session or the
    /// other subscribers (it may run on a reader thread, where an escaping exception would crash the process).
    /// </summary>
    private void Raise(SessionSnapshot? snapshot)
    {
        if (snapshot is null || Changed is not { } changed)
        {
            return;
        }
        foreach (var handler in changed.GetInvocationList().Cast<Action<SessionSnapshot>>())
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception ex)
            {
                DiagLog.Write("RunSession: Changed subscriber threw", ex);
            }
        }
    }

    private sealed class PendingReload(int id, bool fullRestart)
    {
        public int Id { get; } = id;

        public bool FullRestart { get; } = fullRestart;

        public TaskCompletionSource<ReloadOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Per-process state; mutable fields are guarded by <c>_gate</c>.</summary>
    private sealed class Context(RunRequest request)
    {
        public RunRequest Request { get; } = request;

        public IProcessHandle? Handle;
        public string? AppId;
        public bool SupportsRestart;
        public string? ReloadRejection;
        public bool StdoutInException;
        public bool StderrInException;
        public string? LastStderr;
        public PendingReload? Pending;
        public bool StopRequested;
    }
}
