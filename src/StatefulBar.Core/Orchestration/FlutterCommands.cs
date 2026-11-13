using System.ComponentModel;
using StatefulBar.Core.Logging;
using StatefulBar.Core.Processes;
using StatefulBar.Core.Sessions;

namespace StatefulBar.Core.Orchestration;

/// <summary>The project commands of the Flutter flyout.</summary>
public enum FlutterCommand
{
    PubGet,
    Clean,
    CleanAndPubGet,
    BuildRunner,
    Doctor,
}

/// <summary>One process of a command: <c>flutter …</c> or <c>dart …</c> (resolved through fvm like <c>flutter run</c>).</summary>
public sealed record FlutterCommandStep(bool Dart, IReadOnlyList<string> Args, TimeSpan Timeout)
{
    public string Display => (Dart ? "dart " : "flutter ") + string.Join(' ', Args);
}

/// <summary>What each command runs, how it is labelled, and when it may run next to the session.</summary>
public static class FlutterCommands
{
    public const string StopAppFirst = "Stop the app first";
    public const string WaitForStart = "Wait until the app has started";
    public const string WaitForStop = "Wait until the app has stopped";

    private static readonly FlutterCommandStep PubGetStep = new(false, ["pub", "get"], TimeSpan.FromMinutes(5));
    private static readonly FlutterCommandStep CleanStep = new(false, ["clean"], TimeSpan.FromMinutes(2));

    public static string Title(FlutterCommand command) => command switch
    {
        FlutterCommand.PubGet => "Pub get",
        FlutterCommand.Clean => "Clean",
        FlutterCommand.CleanAndPubGet => "Clean and pub get",
        FlutterCommand.BuildRunner => "Run build_runner",
        FlutterCommand.Doctor => "Doctor",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    /// <summary>Short form of what runs, for the flyout's detail column and the log separator.</summary>
    public static string Display(FlutterCommand command) => command switch
    {
        FlutterCommand.CleanAndPubGet => "flutter clean + pub get",
        _ => Steps(command)[0].Display,
    };

    public static IReadOnlyList<FlutterCommandStep> Steps(FlutterCommand command) => command switch
    {
        FlutterCommand.PubGet => [PubGetStep],
        FlutterCommand.Clean => [CleanStep],
        FlutterCommand.CleanAndPubGet => [CleanStep, PubGetStep],
        FlutterCommand.BuildRunner => [new(true, ["run", "build_runner", "build", "--delete-conflicting-outputs"], TimeSpan.FromMinutes(10))],
        FlutterCommand.Doctor => [new(false, ["doctor"], TimeSpan.FromMinutes(2))],
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    /// <summary>
    /// Null when <paramref name="command"/> may run in session <paramref name="state"/>, else why not. clean deletes build/
    /// under a running app; pub get / build_runner would race the build while flutter is starting or stopping.
    /// </summary>
    public static string? Blocked(FlutterCommand command, SessionState state)
    {
        var sessionActive = state is not (SessionState.Idle or SessionState.Failed);
        return command switch
        {
            FlutterCommand.Clean or FlutterCommand.CleanAndPubGet when sessionActive => StopAppFirst,
            FlutterCommand.PubGet or FlutterCommand.BuildRunner when state == SessionState.Starting => WaitForStart,
            FlutterCommand.PubGet or FlutterCommand.BuildRunner when state == SessionState.Stopping => WaitForStop,
            _ => null,
        };
    }
}

public enum FlutterCommandOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <param name="FirstSeq">Sequence number of the separator written before the command's output (to open the log there).</param>
public sealed record FlutterCommandResult(FlutterCommandOutcome Outcome, string Message, long FirstSeq);

/// <summary>
/// Runs a <see cref="FlutterCommand"/> in a project and streams its output into the <see cref="LogBuffer"/> (stdout as tool
/// lines, stderr as errors), after a separator. Steps run one after another and stop at the first failure. The launcher
/// must not use a kill-on-close job: daemons a command starts (adb from <c>flutter doctor</c>) are shared with the session.
/// </summary>
public sealed class FlutterCommandRunner(IProcessLauncher launcher, FlutterToolLocator locator, LogBuffer log)
{
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(5);

    public async Task<FlutterCommandResult> RunAsync(string projectRoot, FlutterCommand command, CancellationToken ct)
    {
        var first = log.MarkCommand(FlutterCommands.Display(command)).Seq;
        foreach (var step in FlutterCommands.Steps(command))
        {
            var failure = await RunStepAsync(projectRoot, step, ct).ConfigureAwait(false);
            if (failure is not null)
            {
                return failure with { FirstSeq = first };
            }
        }
        var done = $"{FlutterCommands.Display(command)} finished";
        log.Append(LogKind.Tool, done);
        return new FlutterCommandResult(FlutterCommandOutcome.Succeeded, done, first);
    }

    // Null when the step exited with 0.
    private async Task<FlutterCommandResult?> RunStepAsync(string projectRoot, FlutterCommandStep step, CancellationToken ct)
    {
        var resolution = step.Dart ? locator.ResolveDart(projectRoot, step.Args) : locator.Resolve(projectRoot, step.Args);
        if (resolution.Spec is not { } spec)
        {
            return Fail(resolution.Error ?? $"{step.Display}: tool not found");
        }

        IProcessHandle handle;
        try
        {
            handle = launcher.Start(spec);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return Fail($"{step.Display} couldn't start: {ex.Message}");
        }

        await using (handle.ConfigureAwait(false))
        {
            handle.StdoutLine += line => log.Append(LogKind.Tool, line);
            handle.StderrLine += line => log.Append(LogKind.Error, line);
            handle.CloseStdin(); // non-interactive: a prompt sees EOF instead of hanging

            using var timeout = new CancellationTokenSource(step.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            int exitCode;
            try
            {
                exitCode = await handle.Exited.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                handle.KillTree();
                await Task.WhenAny(handle.Exited, Task.Delay(DrainWait, CancellationToken.None)).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    var cancelled = $"{step.Display} cancelled";
                    log.Append(LogKind.Tool, cancelled);
                    return new FlutterCommandResult(FlutterCommandOutcome.Cancelled, cancelled, 0);
                }
                return Fail($"{step.Display} timed out after {step.Timeout.TotalMinutes:0} min");
            }
            return exitCode == 0 ? null : Fail($"{step.Display} failed (exit code {exitCode})");
        }
    }

    private FlutterCommandResult Fail(string message)
    {
        log.Append(LogKind.Error, message);
        return new FlutterCommandResult(FlutterCommandOutcome.Failed, message, 0);
    }
}
