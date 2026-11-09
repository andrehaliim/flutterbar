using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Flyouts;
using FlutterBar.App.Log;
using FlutterBar.App.Widget;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Projects;
using FlutterBar.Core.Sessions;

namespace FlutterBar.App.Services;

/// <summary>
/// Flutter flyout (spec §7.5): pub get / clean / build_runner / doctor in the current project, one at a time, output in the
/// log; and "Open terminal" in the project folder. A command keeps running when the flyout closes (Cancel is in the flyout).
/// </summary>
internal sealed partial class AppController
{
    private FlutterFlyout? _flutterFlyout;
    private FlutterCommand? _flutterRunning;
    private CancellationTokenSource? _flutterCts;
    private bool _usesBuildRunner;
    private string _terminalName = TerminalLauncher.WindowsTerminal;

    /// <summary>"Open terminal" from the right-click menu.</summary>
    public async Task OpenTerminalAsync()
    {
        _flyout.Close();
        if (Settings.CurrentProject is not { } root)
        {
            ShowToast(UiText.NeedProject);
            return;
        }
        await RefreshProcessPathAsync(); // the terminal inherits FlutterBar's PATH: make it the current one
        _paths.ClearCache();
        var choice = await Task.Run(() => TerminalLauncher.Resolve(_paths, root));
        try
        {
            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo(choice.Spec.FileName)
                {
                    UseShellExecute = false,
                    WorkingDirectory = choice.Spec.WorkingDirectory,
                };
                foreach (var arg in choice.Spec.Args)
                {
                    psi.ArgumentList.Add(arg);
                }
                Process.Start(psi)?.Dispose(); // detached: the terminal outlives FlutterBar
            });
            AppLog.Write($"terminal opened ({choice.Name}) in {root}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"opening {choice.Name} in {root} failed", ex);
            ShowToast($"Couldn't open {choice.Name}: {ex.Message}");
        }
    }

    /// <summary>"Flutter tools" from the right-click menu (the only way in at the Buttons only size).</summary>
    public void OpenFlutterToolsFromMenu() => ShowFlyoutFor(WidgetChip.Flutter, null);

    private void OpenFlutterFlyout()
    {
        if (Settings.CurrentProject is not { } root)
        {
            ShowFlyoutFor(WidgetChip.Project, UiText.NeedProject);
            return;
        }
        var flyout = _flutterFlyout;
        if (flyout is null)
        {
            flyout = _flutterFlyout = new FlutterFlyout();
            flyout.CommandChosen += command => Fire(RunFlutterCommandAsync(command), FlutterCommands.Title(command));
            flyout.TerminalChosen += () => Fire(OpenTerminalAsync(), "Open terminal");
            flyout.CancelRequested += CancelFlutterCommand;
        }
        RefreshFlutterFlyout();
        var token = ShowFlyout(WidgetChip.Flutter, flyout, flyout.FocusTarget);
        Fire(LoadFlutterFlyoutAsync(root, token), "Read pubspec.yaml");
    }

    // build_runner only shows when the project depends on it; the terminal label depends on what is installed.
    private async Task LoadFlutterFlyoutAsync(string root, CancellationToken token)
    {
        var usesBuildRunner = await Task.Run(() =>
        {
            var pubspec = Path.Combine(root, "pubspec.yaml");
            try
            {
                return File.Exists(pubspec) && PubspecInspector.UsesBuildRunner(File.ReadAllText(pubspec));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"{pubspec} unreadable: build_runner hidden", ex);
                return false;
            }
        });
        _terminalName = await Task.Run(() => TerminalLauncher.Resolve(_paths, root).Name);
        if (!token.IsCancellationRequested && SameProject(root, Settings.CurrentProject))
        {
            _usesBuildRunner = usesBuildRunner;
            RefreshFlutterFlyout();
        }
    }

    /// <summary>Redraws the Flutter flyout when it is open (session state changed, a command started or ended).</summary>
    private void RefreshFlutterFlyout() =>
        _flutterFlyout?.SetState(new FlutterFlyoutState(_session.Snapshot.State, _flutterRunning, _usesBuildRunner, _terminalName));

    private async Task RunFlutterCommandAsync(FlutterCommand command)
    {
        if (Settings.CurrentProject is not { } root || _flutterRunning is not null)
        {
            return;
        }
        if (FlutterCommands.Blocked(command, _session.Snapshot.State) is { } reason)
        {
            ShowToast(reason); // the session changed between drawing the flyout and the click
            return;
        }
        if ((await _tools).Run.IsPreparing)
        {
            ShowToast(FlutterCommands.WaitForStart);
            return;
        }

        var display = FlutterCommands.Display(command);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _flutterCts = cts;
        _flutterRunning = command;
        _vm.FlutterToolTip = $"Running {display}…";
        RefreshFlutterFlyout();
        AppLog.Write($"flutter command {display} in {root}");
        FlutterCommandResult result;
        try
        {
            var runner = new FlutterCommandRunner(new ProcessLauncher(ProcessContainment.None), _locator, _log);
            result = await Task.Run(() => runner.RunAsync(root, command, cts.Token));
        }
        finally
        {
            _flutterCts = null;
            _flutterRunning = null;
            _vm.FlutterToolTip = WidgetViewModel.DefaultFlutterToolTip;
            RefreshFlutterFlyout();
        }

        AppLog.Write($"flutter command {display}: {result.Outcome} - {result.Message}");
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }
        switch (result.Outcome)
        {
            case FlutterCommandOutcome.Succeeded when command == FlutterCommand.Doctor:
                _flyout.Close();
                LogWindow.ShowAt(result.FirstSeq);
                break;
            case FlutterCommandOutcome.Succeeded:
                ShowToast(result.Message);
                break;
            case FlutterCommandOutcome.Failed:
                ShowToast(result.Message, "Open log", () =>
                {
                    LogWindow.ShowAt(result.FirstSeq);
                    return Task.CompletedTask;
                });
                break;
            case FlutterCommandOutcome.Cancelled:
                ShowToast(result.Message);
                break;
        }
    }

    private void CancelFlutterCommand()
    {
        if (_flutterCts is { } cts)
        {
            AppLog.Write($"flutter command {_flutterRunning}: cancel requested");
            cts.Cancel();
        }
    }
}
