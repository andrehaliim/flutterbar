using StatefulBar.App.Diagnostics;
using StatefulBar.App.Widget;
using StatefulBar.Core.Orchestration;
using StatefulBar.Core.Sessions;

namespace StatefulBar.App.Services;

/// <summary>▶ / ⚡ / 🔄 / ⏹ and the one-session confirmation (brief Steps 6-7).</summary>
internal sealed partial class AppController
{
    /// <summary>
    /// ▶: <see cref="RunCoordinator"/> captures project/config/device now, prepares and starts the run, and saves the
    /// result to that project. A selection change meanwhile cancels it or asks first (<see cref="MayChangeSelectionAsync"/>).
    /// </summary>
    public async Task RunAsync()
    {
        var run = (await _tools).Run;
        if (run.IsPreparing)
        {
            return;
        }
        if (_flutterRunning is { } command)
        {
            ShowToast($"Wait for {FlutterCommands.Display(command)} to finish");
            return;
        }
        _flyout.Close();
        _vm.Notice = null;
        _vm.Preparing = true;
        RunOutcome outcome;
        try
        {
            outcome = await run.RunAsync(Settings, () => DateTimeOffset.Now, _lifetime.Token);
        }
        finally
        {
            _vm.Preparing = false;
        }

        AppLog.Write($"run [{outcome.ProjectRoot ?? "-"}]: {outcome.Kind} - {outcome.Message}", outcome.Error);
        if (outcome.Kind == RunOutcomeKind.Cancelled)
        {
            return; // the selection change already said "Run cancelled"
        }
        if (outcome.ShouldPersist)
        {
            _saver.Save("run");
        }
        if (!SameProject(outcome.ProjectRoot, Settings.CurrentProject))
        {
            return; // defensive: never show results of another project
        }
        if (outcome.ShouldPersist && outcome.DeviceId is { } deviceId)
        {
            await RefreshConfigChipAsync(outcome.ProjectRoot!, _generation);
            _vm.DeviceName = DevicePresentation.Label(deviceId, (await _tools).Devices.CachedDevices);
            PublishLabels();
        }

        // Selection problems: no toast (the flyout would cover it), the reason is the flyout's warning line.
        switch (outcome.Kind)
        {
            case RunOutcomeKind.Started:
                break;
            case RunOutcomeKind.StartFailed:
                _vm.Notice = outcome.Message; // the toast comes from the session turning Failed (OnSessionChanged)
                break;
            case RunOutcomeKind.NoProject:
                ShowFlyoutFor(WidgetChip.Project, outcome.Message);
                break;
            case RunOutcomeKind.NoConfig or RunOutcomeKind.ConfigInvalid or RunOutcomeKind.ConfigNotFound:
                ShowFlyoutFor(WidgetChip.Config, outcome.Message);
                break;
            case RunOutcomeKind.NoDevice or RunOutcomeKind.DeviceMissing:
                ShowFlyoutFor(WidgetChip.Device, outcome.Message); // spec §5.4: the device flyout opens, the run does not start
                break;
            default:
                _vm.Notice = outcome.Message;
                ShowToast(outcome.Message);
                break;
        }
    }

    public async Task<ReloadOutcome> ReloadAsync(bool full)
    {
        var label = full ? "Hot restart" : "Hot reload";
        _vm.Notice = null;
        var outcome = await _session.ReloadAsync(full, ReloadTimeout);
        if (!outcome.Success)
        {
            _vm.Notice = $"{label} failed: {outcome.Message}";
            AppLog.Write($"{label} failed ({outcome.Failure}): {outcome.Message}");
            ShowToast(_vm.Notice);
        }
        return outcome;
    }

    public async Task StopAsync()
    {
        _vm.Notice = null;
        await _session.StopAsync();
    }

    /// <summary>
    /// One session at a time: changing project/config/device while not Idle/Failed asks first. Yes = stop, then the caller
    /// changes the selection; nothing is run automatically.
    /// </summary>
    private async Task<bool> ConfirmStopAsync(string what)
    {
        if (!SessionGuard.NeedsStopConfirmation(_session.Snapshot.State))
        {
            return true;
        }
        var yes = await _toast.ConfirmAsync(UiText.ConfirmSwitch, UiText.ConfirmYes, UiText.ConfirmNo);
        AppLog.Write($"switch {what} during a session: {(yes ? "stop and switch" : "kept the session")}");
        if (yes)
        {
            await StopAsync();
        }
        return yes;
    }
}
