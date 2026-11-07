using System.IO;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Services;
using FlutterBar.Core.Config;
using FlutterBar.Core.Orchestration;

namespace FlutterBar.App.Import;

/// <summary>
/// "Import config dari IDE" (spec §4.2), from the menu and from "Belum ada config → Import dari IDE"
/// (<see cref="AppController.ImportRequested"/>). One dialog at a time; discovery and saving run off the UI thread, and the
/// config chip/flyout are refreshed after a save.
/// </summary>
internal sealed class ImportFlow(AppController controller)
{
    private ImportDialog? _dialog;

    public void Open(string? projectRoot)
    {
        if (projectRoot is null)
        {
            controller.ShowToast(UiText.NeedProject);
            return;
        }
        if (_dialog is { } open)
        {
            if (string.Equals(open.ProjectRoot, projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                open.Activate();
                return;
            }
            open.Close();
        }
        var dialog = new ImportDialog(projectRoot);
        dialog.ImportRequested += selected => controller.Observe(SaveAsync(dialog, selected), "Import config");
        dialog.Closed += (_, _) =>
        {
            if (_dialog == dialog)
            {
                _dialog = null;
            }
        };
        _dialog = dialog;
        dialog.Show();
        dialog.Activate();
        AppLog.Write($"import dialog opened for {projectRoot}");
        controller.Observe(LoadAsync(dialog), "Cari config IDE");
    }

    public void CloseIfOpen() => _dialog?.Close();

    private static async Task LoadAsync(ImportDialog dialog)
    {
        dialog.SetBusy(true);
        try
        {
            var root = dialog.ProjectRoot;
            var discovery = await Task.Run(() => ImportPlan.Discover(root));
            if (discovery.Error is not null)
            {
                AppLog.Write($"import discovery in {root}: {discovery.Error}", discovery.Exception);
            }
            else
            {
                AppLog.Write($"import discovery in {root}: {discovery.Rows.Count} candidate(s), {discovery.Rows.Count(r => !r.IsEnabled)} already in launch.json");
            }
            dialog.SetDiscovery(discovery);
        }
        finally
        {
            dialog.SetBusy(false);
        }
    }

    private async Task SaveAsync(ImportDialog dialog, IReadOnlyList<ImportCandidate> selected)
    {
        var root = dialog.ProjectRoot;
        IReadOnlyList<string> added;
        dialog.SetBusy(true);
        try
        {
            added = await Task.Run(() => ImportPlan.Apply(root, selected));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"import into {root} failed", ex);
            dialog.SetError($"Gagal menyimpan launch.json: {ex.Message}");
            return;
        }
        finally
        {
            dialog.SetBusy(false);
        }
        AppLog.Write($"import into {root}: added [{string.Join(", ", added)}]");
        dialog.Close();
        controller.ShowToast(added.Count == 0
            ? "Tidak ada config baru: semua nama sudah ada di launch.json"
            : $"{added.Count} config diimpor: {string.Join(", ", added)}");
        await controller.ReloadConfigAsync(root);
    }
}
