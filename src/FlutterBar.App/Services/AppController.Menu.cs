using System.IO;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Projects;
using FlutterBar.Core.Config;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Projects;

namespace FlutterBar.App.Services;

/// <summary>Right-click menu actions that need the controller's state (Task 16): Kelola project, Rescan, launch.json, import refresh.</summary>
internal sealed partial class AppController
{
    private ManageProjectsDialog? _manageDialog;

    public string? CurrentProject => Settings.CurrentProject;

    /// <summary>Observes a fire-and-forget task from the menu or a dialog: failures are logged and toasted.</summary>
    public void Observe(Task task, string what) => Fire(task, what);

    /// <summary>Yes/no question in a toast above the widget (false when dismissed or when the widget is hidden).</summary>
    public Task<bool> ConfirmAsync(string message, string yes, string no) => _toast.ConfirmAsync(message, yes, no);

    /// <summary>"Rescan": PATH lookups are forgotten (spec §5.2) and the roots are scanned again.</summary>
    public async Task RescanAsync()
    {
        _paths.ClearCache();
        AppLog.Write("rescan requested: PATH lookup cache cleared");
        _manageDialog?.SetBusy(true, "Mencari project…");
        try
        {
            await ScanFreshAsync();
        }
        finally
        {
            _manageDialog?.SetBusy(false, null);
        }
        RefreshManageDialog();
        ShowToast($"Rescan selesai: {_catalog.Count} project");
    }

    /// <summary>"Tambah project…" from the menu: the same flow as the project flyout's button (added and selected).</summary>
    public Task AddProjectFromMenuAsync() => AddProjectAsync();

    /// <summary>"Folder root…" and "Project tersembunyi…": one dialog, scrolled to the section.</summary>
    public void OpenManageProjects(ManageProjectsSection section)
    {
        var dialog = _manageDialog;
        if (dialog is null)
        {
            dialog = _manageDialog = new ManageProjectsDialog();
            dialog.AddRootRequested += path => EditLists(p => ProjectListEditor.AddScanRoot(Settings, p), path, "scan root added", rescan: true);
            dialog.RemoveRootRequested += path => EditLists(p => ProjectListEditor.RemoveScanRoot(Settings, p), path, "scan root removed", rescan: true);
            dialog.AddManualRequested += path => Fire(AddManualFromDialogAsync(path), "Tambah project");
            dialog.RemoveManualRequested += path => EditLists(p => ProjectListEditor.RemoveManual(Settings, p), path, "manual project removed", rescan: false);
            dialog.HideRequested += path => EditLists(p => ProjectListEditor.Hide(Settings, p), path, "project hidden", rescan: false);
            dialog.UnhideRequested += path => EditLists(p => ProjectListEditor.Unhide(Settings, p), path, "project unhidden", rescan: false);
            dialog.RescanRequested += () => Fire(RescanAsync(), "Rescan");
            dialog.Closed += (_, _) => _manageDialog = null;
            RefreshManageDialog();
            dialog.Show();
        }
        dialog.Activate();
        dialog.Reveal(section);
    }

    public void CloseDialogs() => _manageDialog?.Close();

    /// <summary>"Buka launch.json": opens it with the default .json app; when it does not exist yet, offers "Buat default".</summary>
    public async Task OpenLaunchJsonFromMenuAsync()
    {
        if (Settings.CurrentProject is not { } root)
        {
            ShowToast(UiText.NeedProject);
            return;
        }
        var path = LaunchConfigStore.PathFor(root);
        if (await Task.Run(() => File.Exists(path)))
        {
            await OpenLaunchJsonAsync();
            return;
        }
        AppLog.Write($"open launch.json: {path} does not exist, offering the default");
        ShowToast("Belum ada launch.json di project ini", UiText.CreateDefault, async () =>
        {
            await CreateDefaultConfigAsync();
            await OpenLaunchJsonAsync();
        });
    }

    /// <summary>After an import: the chip (and the config flyout when open) show the new configs.</summary>
    public async Task ReloadConfigAsync(string root)
    {
        if (!SameProject(root, Settings.CurrentProject))
        {
            return;
        }
        if (_configFlyout is { } flyout && _flyout.Content == flyout && _flyout.IsOpen)
        {
            await LoadConfigFlyoutAsync(flyout, root, CancellationToken.None);
        }
        else
        {
            await RefreshConfigChipAsync(root, _generation);
        }
    }

    /// <summary>A scan that includes the current roots: one already running may predate a root change, so scan again after it.</summary>
    private async Task ScanFreshAsync()
    {
        if (_scanTask is { IsCompleted: false } running)
        {
            await running;
        }
        await ScanAsync();
    }

    private void EditLists(Func<string, bool> edit, string path, string what, bool rescan)
    {
        if (!edit(path))
        {
            return;
        }
        _saver.Save(what);
        AppLog.Write($"{what}: {path}");
        Fire(AfterListEditAsync(rescan), rescan ? "Scan project" : "Daftar project");
    }

    private async Task AfterListEditAsync(bool rescan)
    {
        _manageDialog?.SetBusy(true, rescan ? "Mencari project…" : null);
        try
        {
            if (rescan)
            {
                await ScanFreshAsync();
            }
            else
            {
                await RebuildCatalogAsync();
            }
        }
        finally
        {
            _manageDialog?.SetBusy(false, null);
        }
        RefreshManageDialog();
    }

    private async Task AddManualFromDialogAsync(string folder)
    {
        if (!await IsFlutterProjectAsync(folder))
        {
            ShowToast($"Bukan project Flutter (pubspec.yaml dengan flutter sdk tidak ada): {folder}");
            return;
        }
        EditLists(p => ProjectListEditor.AddManual(Settings, p), folder, "manual project added", rescan: false);
    }

    private static Task<bool> IsFlutterProjectAsync(string folder) => Task.Run(() =>
    {
        var pubspec = Path.Combine(folder, "pubspec.yaml");
        return File.Exists(pubspec) && PubspecInspector.IsFlutterApp(File.ReadAllText(pubspec));
    });

    private void RefreshManageDialog() =>
        _manageDialog?.SetData(Settings.ScanRoots, Settings.ManualProjects, _catalog, Settings.HiddenProjects);
}
