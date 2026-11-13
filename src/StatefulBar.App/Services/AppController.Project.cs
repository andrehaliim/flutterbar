using System.IO;
using StatefulBar.App.Diagnostics;
using StatefulBar.App.Flyouts;
using StatefulBar.App.Projects;
using StatefulBar.App.Widget;
using StatefulBar.Core.Git;
using StatefulBar.Core.Orchestration;
using StatefulBar.Core.Projects;
using Microsoft.Win32;

namespace StatefulBar.App.Services;

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
    private bool _gitBusy; // a fetch / pull / commit and push is running
    private IReadOnlyList<string> _branches = [];
    private ProjectFlyout? _projectFlyout;
    private BranchFlyout? _branchFlyout;
    private int _branchFlyoutGeneration = -1; // project the shared branch flyout last showed

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
            ShowToast(UiText.CapLines(outcome.Message)); // git stderr (spec §5.5), capped; the full text is in diag.log
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
                Fire(RefreshGitAsync(root, generation, watch: false), "Read branch");
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
        _scanned = await Task.Run(() => ProjectScanner.Scan(
            roots, onSkipped: (path, ex) => AppLog.Write($"scan: skipped unreadable folder {path}", ex)));
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
            flyout.ProjectChosen += path => Fire(SelectProjectAsync(path), "Select project");
            flyout.AddRequested += () => Fire(AddProjectAsync(), "Add project");
        }
        flyout.Reset();
        flyout.SetNotice(notice);
        var scanning = _scanTask is { IsCompleted: false };
        flyout.SetProjects(_catalog, Settings.CurrentProject, scanning); // cache first
        flyout.SetBusy(scanning);
        var token = ShowFlyout(WidgetChip.Project, flyout, flyout.FocusTarget);
        Fire(RefreshProjectFlyoutAsync(flyout, token), "Scan projects");
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

    /// <summary>"Add project…": a folder with a Flutter pubspec.yaml goes to <c>manualProjects</c> and is selected.</summary>
    private async Task AddProjectAsync()
    {
        _flyout.Close();
        var dialog = new OpenFolderDialog { Title = "Select a Flutter project folder" };
        string folder;
        using (var owner = new DialogOwnerWindow()) // never the taskbar-owned widget (see DialogOwnerWindow)
        {
            if (dialog.ShowDialog(owner.Window) != true)
            {
                return;
            }
            folder = dialog.FolderName;
        }
        if (!await IsFlutterProjectAsync(folder))
        {
            ShowToast($"Not a Flutter project (pubspec.yaml with a flutter sdk dependency is missing): {folder}");
            return;
        }
        if (ProjectListEditor.AddManual(Settings, folder))
        {
            _saver.Save("manual project added");
        }
        await RebuildCatalogAsync();
        RefreshManageDialog();
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
            flyout.BranchChosen += branch => Fire(SwitchBranchAsync(branch), "Switch branch");
            flyout.FetchRequested += () => Fire(RunGitActionAsync(GitAction.Fetch, null), "Fetch");
            flyout.PullRequested += () => Fire(RunGitActionAsync(GitAction.Pull, null), "Pull");
            flyout.CommitAndPushRequested += message => Fire(RunGitActionAsync(GitAction.CommitAndPush, message), "Commit and push");
            flyout.PushRequested += () => Fire(RunGitActionAsync(GitAction.Push, null), "Push");
        }
        flyout.Reset();
        if (_branchFlyoutGeneration != _generation)
        {
            // Another project: the typed message and the counts belong to the old one.
            flyout.ResetForProject();
            _branchFlyoutGeneration = _generation;
        }
        flyout.SetBranches(_branches, _gitState.Branch);
        var token = ShowFlyout(WidgetChip.Branch, flyout, flyout.FocusTarget);
        Fire(LoadBranchesAsync(flyout, root, _generation, token), "Branch list");
        Fire(RefreshGitStatusAsync(flyout, root, _generation), "Git status");
    }

    private enum GitAction
    {
        Fetch,
        Pull,
        CommitAndPush,
        Push,
    }

    /// <summary>Changed files and ahead/behind for the flyout; unreadable status hides them.</summary>
    private async Task RefreshGitStatusAsync(BranchFlyout flyout, string root, int generation)
    {
        var git = (await _tools).Git;
        if (git is null)
        {
            return;
        }
        GitStatusInfo? status;
        try
        {
            status = await Task.Run(() => git.GetStatusAsync(root, _lifetime.Token));
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Write($"git status of {root} failed", ex);
            status = null;
        }
        if (generation == _generation)
        {
            flyout.SetStatus(status);
        }
    }

    /// <summary>
    /// Fetch, pull, push or commit and push from the branch flyout. One at a time. While the flyout is open the result is
    /// shown inside it (a toast would cover it); a toast is only used when it was closed meanwhile. After a pull that moved
    /// HEAD of a live session the pull advice replaces the plain "Pulled". Git output is capped at a few lines (the full
    /// text is in diag.log). Results are dropped when the project changed meanwhile.
    /// </summary>
    private async Task RunGitActionAsync(GitAction action, string? commitMessage)
    {
        var root = Settings.CurrentProject;
        var git = (await _tools).Git;
        var flyout = _branchFlyout;
        if (_gitBusy || root is null || git is null || flyout is null)
        {
            return;
        }
        _gitBusy = true;
        flyout.SetOperationBusy(true);
        flyout.SetMessage(null);
        flyout.ClearResult();
        var generation = _generation;
        try
        {
            var live = SessionGuard.IsLive(_session.Snapshot.State);
            var ct = _lifetime.Token;
            var outcome = await Task.Run(async () => action switch
            {
                GitAction.Fetch => ToOutcome(await git.FetchAsync(root, ct)),
                GitAction.Pull => await BranchFlow.PullAsync(git, root, live, ct),
                GitAction.Push => ToOutcome(await git.PushAsync(root, ct)),
                _ => ToOutcome(await git.CommitAndPushAsync(root, commitMessage ?? "", ct)),
            });
            if (generation != _generation)
            {
                AppLog.Write($"git {action} in {root} finished after a project change ({outcome.Message}): result dropped");
                return;
            }
            if (outcome.AdviceError is { } adviceError)
            {
                AppLog.Write("git pull: pubspec comparison skipped", adviceError);
            }
            AppLog.Write($"git {action}: success={outcome.Success} message={outcome.Message} advice={outcome.Advice?.ToString() ?? "-"}");
            var pushed = action is GitAction.CommitAndPush or GitAction.Push;
            if (outcome.Success && pushed)
            {
                flyout.ClearCommitMessage();
            }
            else if (!outcome.Success && outcome.Message.StartsWith(GitService.CommittedLocallyPrefix, StringComparison.Ordinal))
            {
                flyout.ClearCommitMessage(); // the commit exists; the next click only pushes
            }
            var text = UiText.CapLines(outcome.Message);
            if (!outcome.Success)
            {
                flyout.SetMessage(text);
            }
            await RefreshGitStatusAsync(flyout, root, generation);
            await RefreshGitAsync(root, generation, watch: false); // e.g. the first commit ends "(no commits yet)"
            if (generation != _generation)
            {
                return;
            }
            PresentGitResult(flyout, action, outcome, text);
        }
        finally
        {
            _gitBusy = false;
            flyout.SetOperationBusy(false);
        }
    }

    /// <summary>Inline in the open branch flyout, else a toast.</summary>
    private void PresentGitResult(BranchFlyout flyout, GitAction action, BranchSwitchOutcome outcome, string text)
    {
        var open = _flyout.IsOpen && ReferenceEquals(_flyout.Content, flyout);
        if (outcome.Success && outcome.Advice is { } advice)
        {
            var (adviceText, label) = BranchFlow.AdviceToast(advice, afterPull: action == GitAction.Pull);
            Func<Task>? reload = label is null ? null : () => ReloadAsync(full: true);
            if (open)
            {
                flyout.ShowAdvice(adviceText, label, reload is null ? null : () =>
                {
                    _flyout.Close();
                    Fire(reload(), "Hot restart");
                });
            }
            else
            {
                ShowToast(adviceText, label, reload);
            }
        }
        else if (outcome.Success)
        {
            if (open)
            {
                flyout.ShowResult(text);
            }
            else
            {
                ShowToast(text);
            }
        }
        else if (!open)
        {
            ShowToast(text); // when open, the flyout's Message line already shows it
        }
    }

    private static BranchSwitchOutcome ToOutcome(GitResult result) => new(result.Success, result.Message, null);

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
