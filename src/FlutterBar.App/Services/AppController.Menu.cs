using System.IO;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Projects;
using FlutterBar.App.SystemIntegration;
using FlutterBar.Core.Config;
using FlutterBar.Core.Integration;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Projects;

namespace FlutterBar.App.Services;

/// <summary>Right-click menu actions that need the controller's state (Task 16): Manage projects, Rescan, launch.json, import refresh.</summary>
internal sealed partial class AppController
{
    private ManageProjectsDialog? _manageDialog;

    public string? CurrentProject => Settings.CurrentProject;

    /// <summary>Observes a fire-and-forget task from the menu or a dialog: failures are logged and toasted.</summary>
    public void Observe(Task task, string what) => Fire(task, what);

    /// <summary>False while the docking hides the widget (fullscreen / auto-hide): toasts are then only logged.</summary>
    public bool CanShowToast => WidgetShown();

    /// <summary>Yes/no question in a toast above the widget (false when dismissed or when the widget is hidden).</summary>
    public Task<bool> ConfirmAsync(string message, string yes, string no) => _toast.ConfirmAsync(message, yes, no);

    /// <summary>"Rescan": PATH lookups are forgotten (spec §5.2) and the roots are scanned again.</summary>
    public async Task RescanAsync()
    {
        await RefreshProcessPathAsync();
        _paths.ClearCache();
        AppLog.Write("rescan requested: PATH lookup cache cleared");
        _manageDialog?.SetBusy(true, "Scanning for projects…");
        try
        {
            await ScanFreshAsync();
        }
        finally
        {
            _manageDialog?.SetBusy(false, null);
        }
        RefreshManageDialog();
        ShowToast($"Rescan finished: {_catalog.Count} projects");
    }

    /// <summary>
    /// A process keeps the PATH it started with: re-read machine + user PATH from the registry (read-only) so flutter/fvm
    /// installed or added to PATH since FlutterBar started are found, and flutter's own child processes inherit it too.
    /// Session-only entries of the current PATH are kept (<see cref="EnvironmentPath.Compose"/>).
    /// </summary>
    private static async Task RefreshProcessPathAsync()
    {
        try
        {
            var path = await Task.Run(() =>
            {
                var machine = WindowsUserRegistry.ReadMachinePath();
                var user = new WindowsUserRegistry().Read(UserPathEditor.SubKey, UserPathEditor.ValueName)?.Data;
                var fresh = EnvironmentPath.Compose(machine, user, Environment.GetEnvironmentVariable("PATH"));
                Environment.SetEnvironmentVariable("PATH", fresh);
                return fresh;
            });
            AppLog.Write($"rescan: process PATH re-read from the registry ({path.Split(';').Length} entries)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Write("rescan: PATH could not be re-read from the registry, keeping the current one", ex);
        }
    }

    /// <summary>"Add project…" from the menu: the same flow as the project flyout's button (added and selected).</summary>
    public Task AddProjectFromMenuAsync() => AddProjectAsync();

    /// <summary>"Root folders…" and "Hidden projects…": one dialog, scrolled to the section.</summary>
    public void OpenManageProjects(ManageProjectsSection section)
    {
        var dialog = _manageDialog;
        if (dialog is null)
        {
            dialog = _manageDialog = new ManageProjectsDialog();
            dialog.AddRootRequested += path => EditLists(p => ProjectListEditor.AddScanRoot(Settings, p), path, "scan root added", rescan: true);
            dialog.RemoveRootRequested += path => EditLists(p => ProjectListEditor.RemoveScanRoot(Settings, p), path, "scan root removed", rescan: true);
            dialog.AddManualRequested += path => Fire(AddManualFromDialogAsync(path), "Add project");
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

    /// <summary>"Open launch.json": opens it with the default .json app; when it does not exist yet, offers "Create default".</summary>
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
        ShowToast("No launch.json in this project yet", UiText.CreateDefault, async () =>
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
        bool changed;
        try
        {
            changed = edit(path);
        }
        catch (ArgumentException ex) // a path that cannot be normalized (e.g. relative): say which one
        {
            AppLog.Write($"{what}: rejected \"{path}\"", ex);
            ShowToast(ex.Message);
            return;
        }
        if (!changed)
        {
            return;
        }
        _saver.Save(what);
        AppLog.Write($"{what}: {path}");
        Fire(AfterListEditAsync(rescan), rescan ? "Scan projects" : "Project list");
    }

    private async Task AfterListEditAsync(bool rescan)
    {
        _manageDialog?.SetBusy(true, rescan ? "Scanning for projects…" : null);
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
            ShowToast($"Not a Flutter project (pubspec.yaml with a flutter sdk dependency is missing): {folder}");
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
