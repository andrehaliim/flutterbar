using FlutterBar.App.Diagnostics;
using FlutterBar.App.Widget;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Sessions;

namespace FlutterBar.App.Services;

/// <summary>▶ / ⚡ / 🔄 / ⏹ and the one-session confirmation (brief Steps 6-7).</summary>
internal sealed partial class AppController
{
    /// <summary>▶: the pipeline lives in <see cref="RunOrchestrator"/>; here only the outcome is applied.</summary>
    public async Task RunAsync()
    {
        if (_vm.Preparing)
        {
            return;
        }
        _flyout.Close();
        _vm.Notice = null;
        _vm.Preparing = true;
        RunOutcome outcome;
        try
        {
            var root = Settings.CurrentProject;
            ProjectStateView(root, out var config, out var device);
            var orchestrator = (await _tools).Orchestrator;
            outcome = await orchestrator.RunAsync(new RunSelection(root, config, device), _lifetime.Token);
        }
        finally
        {
            _vm.Preparing = false;
        }

        AppLog.Write($"run: {outcome.Kind} - {outcome.Message}", outcome.Error);
        if (outcome.ShouldPersist && Settings.CurrentProject is { } project && outcome.Config is { } used && outcome.DeviceId is { } deviceId)
        {
            ProjectSelection.RecordRun(Settings, project, used.Name, deviceId, DateTimeOffset.Now);
            _saver.Save("run");
            await RefreshConfigChipAsync(project, _generation);
            _vm.DeviceName = DevicePresentation.Label(deviceId, (await _tools).Devices.CachedDevices);
            PublishLabels();
        }

        switch (outcome.Kind)
        {
            case RunOutcomeKind.Started:
                break;
            case RunOutcomeKind.StartFailed:
                _vm.Notice = outcome.Message; // the toast comes from the session turning Failed (OnSessionChanged)
                break;
            case RunOutcomeKind.NoProject:
                ShowToast(outcome.Message);
                OpenFlyout(WidgetChip.Project);
                break;
            case RunOutcomeKind.NoConfig or RunOutcomeKind.ConfigInvalid or RunOutcomeKind.ConfigNotFound:
                ShowToast(outcome.Message);
                OpenFlyout(WidgetChip.Config);
                break;
            case RunOutcomeKind.NoDevice or RunOutcomeKind.DeviceMissing:
                ShowToast(outcome.Message);
                OpenFlyout(WidgetChip.Device); // spec §5.4: the device flyout opens and the run does not start
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
            _vm.Notice = $"{label} gagal: {outcome.Message}";
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

    private void ProjectStateView(string? root, out string? config, out string? device)
    {
        config = null;
        device = null;
        if (root is not null && Settings.TryGetProject(root, out var state))
        {
            config = state.Config;
            device = state.DeviceId;
        }
    }
}
