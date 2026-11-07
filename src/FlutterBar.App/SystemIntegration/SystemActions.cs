using System.IO;
using System.Security;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Services;
using FlutterBar.Core.Integration;
using FlutterBar.Core.Orchestration;

namespace FlutterBar.App.SystemIntegration;

/// <summary>
/// Menu items "Tambahkan CLI ke PATH" and "Start with Windows" (spec §7.4). Registry work and the environment broadcast
/// run off the UI thread; failures are logged and toasted. The PATH/Run logic itself is <c>FlutterBar.Core.Integration</c>.
/// </summary>
internal sealed class SystemActions(AppController ui, IUserRegistry registry)
{
    private const string CliExe = "flutterbar.exe";

    /// <summary>Folder of flutterbar.exe: next to FlutterBar.App.exe (the app project copies the CLI into its output).</summary>
    public static string CliFolder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public static string AppExe => Environment.ProcessPath ?? Path.Combine(CliFolder, "FlutterBar.App.exe");

    public async Task AddCliToPathAsync()
    {
        var folder = CliFolder;
        bool exists;
        bool present;
        try
        {
            (exists, present) = await Task.Run(() => (File.Exists(Path.Combine(folder, CliExe)), UserPathEditor.IsPresent(registry, folder)));
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            Report("reading the user PATH failed", ex, $"PATH user tidak bisa dibaca: {ex.Message}");
            return;
        }
        if (!exists)
        {
            AppLog.Write($"add to PATH: {CliExe} not found in {folder}");
            ui.ShowToast($"{CliExe} tidak ditemukan di {folder}");
            return;
        }
        if (present)
        {
            AppLog.Write($"add to PATH: {folder} already in the user PATH");
            ui.ShowToast($"Folder CLI sudah ada di PATH user: {folder}");
            return;
        }
        if (!ui.CanShowToast)
        {
            AppLog.Write("add to PATH: toast tidak bisa ditampilkan (widget hidden by fullscreen/auto-hide), PATH not changed");
            return;
        }
        if (!await ui.ConfirmAsync($"Tambahkan {folder} ke PATH user?", "Tambahkan", UiText.ConfirmNo))
        {
            AppLog.Write("add to PATH: dibatalkan user (Batal, closed, or replaced by another toast), PATH not changed");
            return;
        }
        try
        {
            var outcome = await Task.Run(() => UserPathEditor.AddFolder(registry, folder));
            AppLog.Write($"add to PATH: {folder} -> {outcome}");
            if (outcome == PathAddOutcome.Added)
            {
                var error = await Task.Run(EnvironmentBroadcast.Send);
                AppLog.Write(error is null ? "WM_SETTINGCHANGE Environment broadcast sent" : $"WM_SETTINGCHANGE Environment broadcast failed or timed out (win32 error {error})");
            }
            ui.ShowToast(UiText.PathAdded);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            Report($"adding {folder} to the user PATH failed", ex, $"PATH user gagal diubah: {ex.Message}");
        }
    }

    /// <summary>Actual Run-key state (any value = on); null when it cannot be read (logged).</summary>
    public async Task<StartupState?> ReadStartupAsync()
    {
        try
        {
            return await Task.Run(() => StartupRegistration.Read(registry, AppExe));
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            AppLog.Write("reading the Start with Windows state failed", ex);
            return null;
        }
    }

    /// <summary>Turns Start with Windows on/off and returns the state read back afterwards (null when unreadable).</summary>
    public async Task<StartupState?> SetStartupAsync(bool on)
    {
        try
        {
            await Task.Run(() =>
            {
                if (on)
                {
                    StartupRegistration.Enable(registry, AppExe);
                }
                else
                {
                    StartupRegistration.Disable(registry);
                }
            });
            AppLog.Write($"Start with Windows {(on ? "enabled: " + StartupRegistration.CommandFor(AppExe) : "disabled")}");
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            Report($"Start with Windows {(on ? "enable" : "disable")} failed", ex, $"Start with Windows gagal diubah: {ex.Message}");
        }
        return await ReadStartupAsync();
    }

    private static bool IsRegistryFailure(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or SecurityException or InvalidDataException or ArgumentException;

    private void Report(string log, Exception ex, string toast)
    {
        AppLog.Write(log, ex);
        ui.ShowToast(toast);
    }
}
