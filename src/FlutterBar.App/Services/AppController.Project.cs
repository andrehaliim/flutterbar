using System.IO;
using FlutterBar.App.Diagnostics;
using FlutterBar.App.Flyouts;
using FlutterBar.App.Widget;
using FlutterBar.Core.Git;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Projects;
using Microsoft.Win32;

namespace FlutterBar.App.Services;

/// <summary>Project selection, the project scan/catalog and the branch chip (brief Steps 1-3).</summary>
internal sealed partial class AppController
{
    private IReadOnlyList<string> _scanned = [];
    private IReadOnlyList<ProjectInfo> _catalog = [];
    private DateTimeOffset? _lastScan;
    private Task? _scanTask;
    private int _generation; // bumped on every project change: results for an older project are dropped
    private HeadWatcher? _headWatcher;
    private GitState? _gitState;
    private IReadOnlyList<string> _branches = [];
    private ProjectFlyout? _projectFlyout;
    private BranchFlyout? _branchFlyout;

    public async Task SelectProjectAsync(string path)
    {
        _flyout.Close();
        if (Settings.CurrentProject is { } current && string.Equals(Path.GetFullPath(path).TrimEnd('\\'), current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (!await MayChangeSelectionAsync("project"))
        {
            return;
        }
        ProjectSelection.SelectProject(Settings, path);
        _saver.Save("project");
        AppLog.Write($"project selected: {Settings.CurrentProject}");
        await ApplyProjectAsync(Settings.CurrentProject);
    }

    public async Task SwitchBranchAsync(string branch)
    {
        _flyout.Close();
        var root = Settings.CurrentProject;
        var git = (await _tools).Git;
        if (root is null || git is null || branch == _gitState?.Branch)
        {
            return;
        }
        var generation = _generation;
        var live = SessionGuard.IsLive(_session.Snapshot.State);
        var outcome = await Task.Run(() => BranchFlow.SwitchAsync(git, root, branch, live));
        if (generation != _generation)
        {
            AppLog.Write($"git switch {branch} in {root} finished after a project change ({outcome.Message}): result dropped");
            return;
        }
        if (outcome.AdviceError is { } adviceError)
        {
            AppLog.Write("branch switch: pubspec comparison skipped", adviceError);
        }
        if (!outcome.Success)
        {
            AppLog.Write($"git switch {branch} failed: {outcome.Message}");
            ShowToast(outcome.Message); // git stderr as is (spec §5.5)
            return;
        }
        AppLog.Write($"git switch {branch}: {outcome.Message} advice={outcome.Advice?.ToString() ?? "-"}");
        await RefreshGitAsync(root, generation, watch: false);
        if (outcome.Advice is { } advice)
        {
            var (text, label) = BranchFlow.AdviceToast(advice);
            ShowToast(text, label, label is null ? null : () => ReloadAsync(full: true));
        }
    }

    /// <summary>Shows the project's labels: name, config chip, device, branch chip and HEAD watcher.</summary>
    private async Task ApplyProjectAsync(string? root)
    {
        var generation = ++_generation;
        DisposeHeadWatcher();
        _gitState = null;
        _branches = [];
        _configChip = null;
        if (root is null)
        {
            _vm.ProjectName = UiText.PickProject;
            _vm.BranchVisible = false;
            _vm.ConfigName = UiText.PickConfig;
            _vm.ConfigHasError = false;
            _vm.ConfigToolTip = null;
            _vm.DeviceName = UiText.PickDevice;
            PublishLabels();
            return;
        }
        _vm.ProjectName = _catalog.FirstOrDefault(p => string.Equals(p.Path, root, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? Path.GetFileName(root.TrimEnd('\\', '/'));
        Settings.TryGetProject(root, out var state);
        _vm.DeviceName = DevicePresentation.Label(state?.DeviceId, (await _tools).Devices.CachedDevices);
        PublishLabels();
        await Task.WhenAll(RefreshConfigChipAsync(root, generation), RefreshGitAsync(root, generation, watch: true));
    }

    private async Task RefreshGitAsync(string root, int generation, bool watch)
    {
        var git = (await _tools).Git;
        if (git is null)
        {
            _vm.BranchVisible = false;
            PublishLabels();
            return;
        }
        GitState state;
        try
        {
            state = await Task.Run(() => BranchFlow.ReadAsync(git, root));
        }
        catch (Exception ex) when (ToolFailure.IsExpected(ex))
        {
            AppLog.Write($"git state of {root} unreadable, branch chip hidden", ex);
            state = new GitState(false, null, false, ex.Message);
        }
        if (generation != _generation)
        {
            return;
        }
        _gitState = state;
        _vm.BranchVisible = state.IsRepo;
        _vm.Branch = state.Branch ?? "?";
        _vm.BranchToolTip = state.ToolTip;
        if (state.Unborn)
        {
            AppLog.Limited("unborn:" + root, $"git: {root} has no commit yet, branch from symbolic-ref = {state.Branch}", 60);
        }
        if (state.Error is not null)
        {
            AppLog.Limited("git-error:" + root, $"git: branch of {root} unreadable: {state.Error}", 60);
        }
        PublishLabels();
        if (watch && state.IsRepo)
        {
            await WatchHeadAsync(git, root, generation);
        }
    }

    // HEAD of the worktree (git rev-parse --git-path HEAD) changes when the branch is switched from outside.
    private async Task WatchHeadAsync(GitService git, string root, int generation)
    {
        HeadWatcher watcher;
        try
        {
            var head = await Task.Run(() => git.GetHeadFilePathAsync(root));
            watcher = await Task.Run(() => new HeadWatcher(head));
        }
        catch (Exception ex) when (ToolFailure.IsExpected(ex) || ex is ArgumentException or UnauthorizedAccessException)
        {
            AppLog.Write($"HEAD watcher for {root} not started: branch changes from outside show on the next flyout open", ex);
            return;
        }
        if (generation != _generation || _disposed)
        {
            watcher.Dispose();
            return;
        }
        watcher.Changed += () => _dispatcher.BeginInvoke(() =>
        {
            if (generation == _generation && !_disposed)
            {
                Fire(RefreshGitAsync(root, generation, watch: false), "Baca branch");
            }
        });
        _headWatcher = watcher;
    }

    private void DisposeHeadWatcher()
    {
        _headWatcher?.Dispose();
        _headWatcher = null;
    }

    /// <summary>Scans the roots in the background (one scan at a time) and rebuilds the catalog.</summary>
    private async Task ScanAsync()
    {
        if (_scanTask is { IsCompleted: false } running)
        {
            await running;
            return;
        }
        _scanTask = ScanCoreAsync();
        await _scanTask;
    }

    private async Task ScanCoreAsync()
    {
        var roots = Settings.ScanRoots.ToList();
        var started = DateTimeOffset.UtcNow;
        _scanned = await Task.Run(() => ProjectScanner.Scan(roots));
        _lastScan = DateTimeOffset.UtcNow;
        AppLog.Write($"scan: {_scanned.Count} project(s) in {roots.Count} root(s), {(_lastScan.Value - started).TotalMilliseconds:F0} ms");
        await RebuildCatalogAsync();
    }

    private async Task RebuildCatalogAsync()
    {
        var scanned = _scanned;
        var snapshot = _saver.Snapshot();
        _catalog = await Task.Run(() => ProjectCatalog.Build(scanned, snapshot, Directory.Exists));
    }

    private void OpenProjectFlyout(string? notice = null)
    {
        var flyout = _projectFlyout;
        if (flyout is null)
        {
            flyout = _projectFlyout = new ProjectFlyout();
            flyout.ProjectChosen += path => Fire(SelectProjectAsync(path), "Pilih project");
            flyout.AddRequested += () => Fire(AddProjectAsync(), "Tambah project");
        }
        flyout.Reset();
        flyout.SetNotice(notice);
        var scanning = _scanTask is { IsCompleted: false };
        flyout.SetProjects(_catalog, Settings.CurrentProject, scanning); // cache first
        flyout.SetBusy(scanning);
        var token = ShowFlyout(WidgetChip.Project, flyout, flyout.FocusTarget);
        Fire(RefreshProjectFlyoutAsync(flyout, token), "Scan project");
    }

    private async Task RefreshProjectFlyoutAsync(ProjectFlyout flyout, CancellationToken token)
    {
        flyout.SetBusy(true);
        try
        {
            if (ScanPolicy.IsStale(_lastScan, DateTimeOffset.UtcNow) || _scanTask is { IsCompleted: false })
            {
                await ScanAsync();
            }
            else
            {
                await RebuildCatalogAsync(); // availability (folder gone) and LastUsed order
            }
        }
        finally
        {
            flyout.SetBusy(false);
        }
        if (!token.IsCancellationRequested)
        {
            flyout.SetProjects(_catalog, Settings.CurrentProject, scanning: false);
        }
    }

    /// <summary>"Tambah project…": a folder with a Flutter pubspec.yaml goes to <c>manualProjects</c> and is selected.</summary>
    private async Task AddProjectAsync()
    {
        _flyout.Close();
        var dialog = new OpenFolderDialog { Title = "Pilih folder project Flutter" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var folder = dialog.FolderName;
        var isFlutter = await Task.Run(() =>
        {
            var pubspec = Path.Combine(folder, "pubspec.yaml");
            return File.Exists(pubspec) && PubspecInspector.IsFlutterApp(File.ReadAllText(pubspec));
        });
        if (!isFlutter)
        {
            ShowToast($"Bukan project Flutter (pubspec.yaml dengan flutter sdk tidak ada): {folder}");
            return;
        }
        if (!Settings.ManualProjects.Any(p => string.Equals(Path.GetFullPath(p).TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            Settings.ManualProjects.Add(folder);
            _saver.Save("manual project added");
        }
        await RebuildCatalogAsync();
        await SelectProjectAsync(folder);
    }

    private void OpenBranchFlyout()
    {
        var root = Settings.CurrentProject;
        if (root is null || _gitState is not { IsRepo: true })
        {
            return; // the chip is hidden in that case
        }
        var flyout = _branchFlyout;
        if (flyout is null)
        {
            flyout = _branchFlyout = new BranchFlyout();
            flyout.BranchChosen += branch => Fire(SwitchBranchAsync(branch), "Ganti branch");
        }
        flyout.Reset();
        flyout.SetBranches(_branches, _gitState.Branch);
        var token = ShowFlyout(WidgetChip.Branch, flyout, flyout.FocusTarget);
        Fire(LoadBranchesAsync(flyout, root, _generation, token), "Daftar branch");
    }

    private async Task LoadBranchesAsync(BranchFlyout flyout, string root, int generation, CancellationToken token)
    {
        var git = (await _tools).Git;
        if (git is null)
        {
            return;
        }
        flyout.SetBusy(true);
        try
        {
            var branches = await Task.Run(() => git.GetLocalBranchesAsync(root), token);
            await RefreshGitAsync(root, generation, watch: false); // current branch may have changed from outside
            if (token.IsCancellationRequested || generation != _generation)
            {
                return;
            }
            _branches = branches;
            flyout.SetBranches(branches, _gitState?.Branch);
            flyout.SetMessage(branches.Count == 0 ? UiText.NoCommitYet : null);
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Write($"git branch list of {root} failed", ex);
            flyout.SetMessage(ex.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            AppLog.Write("branch list: flyout closed before git answered");
        }
        finally
        {
            flyout.SetBusy(false);
        }
    }
}
