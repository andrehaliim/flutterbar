using System.IO;
using StatefulBar.App.Diagnostics;
using StatefulBar.App.Services;
using StatefulBar.Core.Config;
using StatefulBar.Core.Orchestration;

namespace StatefulBar.App.Import;

/// <summary>
/// "Import config from IDE" (spec §4.2), from the menu and from "No config yet → Import from IDE"
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
        controller.Observe(LoadAsync(dialog), "Search IDE configs");
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
            dialog.SetError($"Couldn't save launch.json: {ex.Message}");
            return;
        }
        finally
        {
            dialog.SetBusy(false);
        }
        AppLog.Write($"import into {root}: added [{string.Join(", ", added)}]");
        dialog.Close();
        controller.ShowToast(added.Count == 0
            ? "No new configs: all names already exist in launch.json"
            : $"{added.Count} configs imported: {string.Join(", ", added)}");
        await controller.ReloadConfigAsync(root);
    }
}
