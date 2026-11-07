using System.Diagnostics;
using System.IO;
using System.Windows;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Flyouts;
using FlutterBar.App.Widget;
using FlutterBar.Core.Config;
using FlutterBar.Core.Devices;
using FlutterBar.Core.Orchestration;

namespace FlutterBar.App.Services;

/// <summary>Flyout dispatch plus the config and device flyouts (brief Steps 4-5).</summary>
internal sealed partial class AppController
{
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

    private ConfigChipState? _configChip;
    private ConfigFlyout? _configFlyout;
    private DeviceFlyout? _deviceFlyout;
    private CombinedFlyout? _combinedFlyout;
    private WidgetChip? _flyoutKind;
    private WidgetChip? _closedKind;
    private DateTime _closedAt;
    private bool _launchingEmulator;

    public async Task SelectConfigAsync(string name)
    {
        _flyout.Close();
        var root = Settings.CurrentProject;
        if (root is null || name == _configChip?.Selected?.Name)
        {
            return;
        }
        if (!await MayChangeSelectionAsync("config"))
        {
            return;
        }
        ProjectSelection.SelectConfig(Settings, root, name);
        _saver.Save("config");
        await RefreshConfigChipAsync(root, _generation);
    }

    public async Task SelectDeviceAsync(string id)
    {
        _flyout.Close();
        var root = Settings.CurrentProject;
        if (root is null)
        {
            return;
        }
        Settings.TryGetProject(root, out var state);
        if (!string.Equals(state?.DeviceId, id, StringComparison.OrdinalIgnoreCase))
        {
            if (!await MayChangeSelectionAsync("device"))
            {
                return;
            }
            ProjectSelection.SelectDevice(Settings, root, id);
            _saver.Save("device");
        }
        _vm.DeviceName = DevicePresentation.Label(id, (await _tools).Devices.CachedDevices);
        PublishLabels();
    }

    /// <summary>Chip / dot click: opens the matching flyout (a second click on the same chip closes it).</summary>
    private void OpenFlyout(WidgetChip chip)
    {
        // StaysOpen=false closes the popup on the mouse-down of the click that then reaches the chip: don't reopen.
        if (_closedKind == chip && DateTime.UtcNow - _closedAt < ReopenGuard)
        {
            return;
        }
        if (_flyout.IsOpen && _flyoutKind == chip)
        {
            _flyout.Close(); // keyboard / UI Automation invoke: no outside click closed it first
            return;
        }
        ShowFlyoutFor(chip, null);
    }

    /// <summary>Opens a flyout without the click guards (combined flyout rows, ▶ outcomes); <paramref name="notice"/> is its warning line.</summary>
    private void ShowFlyoutFor(WidgetChip chip, string? notice)
    {
        switch (chip)
        {
            case WidgetChip.Project:
                OpenProjectFlyout(notice);
                break;
            case WidgetChip.Branch:
                OpenBranchFlyout();
                break;
            case WidgetChip.Config:
                OpenConfigFlyout(notice);
                break;
            case WidgetChip.Device:
                OpenDeviceFlyout(notice);
                break;
            case WidgetChip.Combined:
                OpenCombinedFlyout();
                break;
        }
    }

    private CancellationToken ShowFlyout(WidgetChip kind, FrameworkElement content, IInputElement focus)
    {
        _flyoutKind = kind;
        return _flyout.Show(content, focus);
    }

    private void OnFlyoutClosed()
    {
        _closedKind = _flyoutKind;
        _closedAt = DateTime.UtcNow;
        _flyoutKind = null;
    }

    private void OpenCombinedFlyout()
    {
        var flyout = _combinedFlyout;
        if (flyout is null)
        {
            flyout = _combinedFlyout = new CombinedFlyout();
            flyout.SectionChosen += chip => ShowFlyoutFor(chip, null);
        }
        flyout.SetValues(_vm.ProjectName, _vm.BranchVisible ? _vm.Branch : null, _vm.ConfigName, _vm.DeviceName);
        ShowFlyout(WidgetChip.Combined, flyout, flyout.FocusTarget);
    }

    /// <summary>Re-reads launch.json (always from disk) and updates the config chip; null when it could not be read.</summary>
    private async Task<(LaunchConfigLoadResult Result, ConfigChipState Chip)?> RefreshConfigChipAsync(string root, int generation)
    {
        LaunchConfigLoadResult result;
        try
        {
            result = await Task.Run(() => LaunchConfigStore.Load(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"launch.json of {root} unreadable", ex);
            if (generation == _generation)
            {
                _vm.ConfigHasError = true;
                _vm.ConfigToolTip = $"launch.json tidak terbaca: {ex.Message}";
            }
            return null;
        }
        if (generation != _generation)
        {
            return null;
        }
        Settings.TryGetProject(root, out var state);
        var chip = ConfigPresentation.Chip(result, state?.Config);
        _configChip = chip;
        _vm.ConfigName = chip.Text;
        _vm.ConfigHasError = chip.IsError;
        _vm.ConfigToolTip = chip.ToolTip;
        PublishLabels();
        return (result, chip);
    }

    private void OpenConfigFlyout(string? notice = null)
    {
        var root = Settings.CurrentProject;
        if (root is null)
        {
            OpenProjectFlyout(notice);
            return;
        }
        var flyout = _configFlyout;
        if (flyout is null)
        {
            flyout = _configFlyout = new ConfigFlyout();
            flyout.ConfigChosen += name => Fire(SelectConfigAsync(name), "Pilih config");
            flyout.CreateDefaultRequested += () => Fire(CreateDefaultConfigAsync(), "Buat default");
            flyout.OpenFileRequested += () => Fire(OpenLaunchJsonAsync(), "Buka file");
            flyout.ImportRequested += OnImportRequested;
        }
        flyout.Reset(); // never show the previous project's configs while launch.json is re-read
        flyout.SetNotice(notice);
        var token = ShowFlyout(WidgetChip.Config, flyout, flyout.FocusTarget);
        Fire(LoadConfigFlyoutAsync(flyout, root, token), "Baca launch.json");
    }

    private async Task LoadConfigFlyoutAsync(ConfigFlyout flyout, string root, CancellationToken token)
    {
        flyout.SetBusy(true);
        try
        {
            var loaded = await RefreshConfigChipAsync(root, _generation);
            if (token.IsCancellationRequested)
            {
                return;
            }
            if (loaded is { } ok)
            {
                flyout.SetResult(ok.Result, ok.Chip);
                _flyout.Refocus(flyout, flyout.FocusTarget); // the focus target depends on what was loaded
            }
            else
            {
                flyout.SetMessage(_vm.ConfigToolTip);
                _flyout.Refocus(flyout, flyout.FocusTarget);
            }
        }
        finally
        {
            flyout.SetBusy(false);
        }
    }

    private void OnImportRequested()
    {
        _flyout.Close();
        if (Settings.CurrentProject is not { } root)
        {
            return;
        }
        if (ImportRequested is { } handler)
        {
            handler(root);
        }
        else
        {
            AppLog.Write($"Import dari IDE requested for {root}: no ImportRequested handler");
        }
    }

    private async Task CreateDefaultConfigAsync()
    {
        var root = Settings.CurrentProject;
        if (root is null)
        {
            return;
        }
        var created = await Task.Run(() =>
        {
            if (File.Exists(LaunchConfigStore.PathFor(root)))
            {
                return false; // written meanwhile (e.g. by hand): never overwrite
            }
            LaunchConfigStore.Save(root, [LaunchConfigStore.CreateDefault()]);
            return true;
        });
        AppLog.Write(created ? $"default launch.json created in {root}" : $"launch.json appeared in {root}, default not written");
        if (_configFlyout is { } flyout && _flyout.Content == flyout && _flyout.IsOpen)
        {
            await LoadConfigFlyoutAsync(flyout, root, CancellationToken.None);
        }
        else
        {
            await RefreshConfigChipAsync(root, _generation);
        }
    }

    // Opens launch.json with the default .json app (spec §7.4); ShellExecute runs off the UI thread.
    private async Task OpenLaunchJsonAsync()
    {
        _flyout.Close();
        if (Settings.CurrentProject is not { } root)
        {
            return;
        }
        var path = LaunchConfigStore.PathFor(root);
        try
        {
            await Task.Run(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"opening {path} failed", ex);
            ShowToast($"Tidak bisa membuka {path}: {ex.Message}");
        }
    }

    private void OpenDeviceFlyout(string? notice = null)
    {
        var root = Settings.CurrentProject;
        if (root is null)
        {
            OpenProjectFlyout(notice);
            return;
        }
        var flyout = _deviceFlyout;
        if (flyout is null)
        {
            flyout = _deviceFlyout = new DeviceFlyout();
            flyout.DeviceChosen += device => Fire(SelectDeviceAsync(device.Id), "Pilih device");
            flyout.EmulatorChosen += emulator => Fire(LaunchEmulatorAsync(emulator.Id), "Nyalakan emulator");
        }
        flyout.SetMessage(null);
        flyout.SetNotice(notice);
        var token = ShowFlyout(WidgetChip.Device, flyout, flyout.FocusTarget);
        Fire(RefreshDeviceFlyoutAsync(flyout, root, token), "Daftar device");
    }

    // Cached list immediately, then devices and emulators refreshed (cancelled when the flyout closes).
    private async Task RefreshDeviceFlyoutAsync(DeviceFlyout flyout, string root, CancellationToken token)
    {
        var devices = (await _tools).Devices;
        Settings.TryGetProject(root, out var state);
        flyout.SetDevices(devices.CachedDevices, devices.OfflineEmulators(), state?.DeviceId, refreshing: true);
        flyout.SetBusy(true);
        var problems = new List<string>();
        try
        {
            await RefreshPartAsync(() => devices.RefreshDevicesAsync(root, token), "flutter devices", problems);
            if (!token.IsCancellationRequested)
            {
                flyout.SetDevices(devices.CachedDevices, devices.OfflineEmulators(), state?.DeviceId, refreshing: true);
                await RefreshPartAsync(() => devices.RefreshEmulatorsAsync(root, token), "flutter emulators", problems);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            AppLog.Write("device refresh cancelled: flyout closed");
            return;
        }
        finally
        {
            flyout.SetBusy(false);
        }
        if (token.IsCancellationRequested)
        {
            return;
        }
        Settings.TryGetProject(root, out state);
        flyout.SetDevices(devices.CachedDevices, devices.OfflineEmulators(), state?.DeviceId, refreshing: false);
        flyout.SetMessage(problems.Count == 0 ? null : string.Join("\n", problems));
        if (state?.DeviceId is { } id)
        {
            _vm.DeviceName = DevicePresentation.Label(id, devices.CachedDevices);
        }
    }

    private static async Task RefreshPartAsync(Func<Task> refresh, string what, List<string> problems)
    {
        try
        {
            await Task.Run(refresh);
        }
        catch (Exception ex) when (ToolFailure.IsExpected(ex))
        {
            AppLog.Write($"{what} failed", ex);
            problems.Add(ex.Message);
        }
    }

    public async Task LaunchEmulatorAsync(string emulatorId)
    {
        _flyout.Close();
        var root = Settings.CurrentProject;
        if (root is null || _launchingEmulator)
        {
            return;
        }
        _launchingEmulator = true;
        try
        {
            ShowToast($"Menyalakan emulator {emulatorId}…");
            var devices = (await _tools).Devices;
            var outcome = await Task.Run(() => EmulatorFlow.LaunchAsync(devices, root, emulatorId, EmulatorFlow.PollInterval, EmulatorFlow.Timeout, _lifetime.Token));
            if (!SameProject(root, Settings.CurrentProject))
            {
                // The emulator stays up; it is just not selected for a project the user left meanwhile.
                AppLog.Write($"emulator {emulatorId} finished after a project change ({outcome.Device?.Id ?? outcome.Message}): not selected");
                return;
            }
            if (outcome.Device is { } device)
            {
                AppLog.Write($"emulator {emulatorId} ready as {device.Id}");
                await SelectDeviceAsync(device.Id);
                ShowToast($"Emulator siap: {device.Name}");
            }
            else
            {
                AppLog.Write($"emulator {emulatorId}: {outcome.Message}", outcome.Error);
                ShowToast(outcome.Message ?? UiText.EmulatorTimeout);
            }
        }
        finally
        {
            _launchingEmulator = false;
        }
    }
}
