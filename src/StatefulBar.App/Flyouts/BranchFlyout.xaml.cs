using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StatefulBar.Core.Git;

namespace StatefulBar.App.Flyouts;

internal sealed partial class BranchFlyout : UserControl
{
    /// <summary>Width of the left column (buttons, message, changed files) while the changes panel is open.</summary>
    private const double ChangesColumnWidth = 300;

    private readonly FlyoutList _list;
    private bool _loading;
    private bool _operating;
    private int _loadingChanges; // list and diff loads may overlap
    private bool _changesOpen;
    private bool _fillingChanges; // SetChanges replaces the list: its selection change is not a user pick
    private GitStatusInfo? _status;
    private Action? _adviceAction;
    private readonly DispatcherTimer _resultTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public BranchFlyout()
    {
        InitializeComponent();
        _list = new FlyoutList(List, Search);
        _list.Chosen += item =>
        {
            if (!_operating && item.Value is string branch)
            {
                BranchChosen?.Invoke(branch);
            }
        };
        _resultTimer.Tick += (_, _) => ClearResult();
        AdviceButton.Click += (_, _) =>
        {
            var action = _adviceAction;
            ClearResult();
            action?.Invoke();
        };
        FetchButton.Click += (_, _) => FetchRequested?.Invoke();
        PullButton.Click += (_, _) => PullRequested?.Invoke();
        PushButton.Click += (_, _) =>
        {
            if (PushButton.IsEnabled)
            {
                PushRequested?.Invoke();
            }
        };
        CommitButton.Click += (_, _) => RaiseCommit();
        CommitMessage.TextChanged += (_, _) => UpdateEnabled();
        CommitMessage.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RaiseCommit();
                e.Handled = true;
            }
        };
        ChangesButton.Click += (_, _) => SetChangesOpen(!_changesOpen);
        CloseChangesButton.Click += (_, _) => SetChangesOpen(false);
        ChangeList.SelectionChanged += (_, _) =>
        {
            if (!_fillingChanges && ChangeList.SelectedItem is GitChangedFile file)
            {
                FileSelected?.Invoke(file);
            }
        };
    }

    public event Action<string>? BranchChosen;

    public event Action? FetchRequested;

    public event Action? PullRequested;

    public event Action<string>? CommitRequested;

    public event Action? PushRequested;

    /// <summary>The changes panel opened (true: load the changed files, widen the flyout) or closed.</summary>
    public event Action<bool>? ChangesToggled;

    /// <summary>A changed file was selected (by the user, or the first one after a load): show its diff.</summary>
    public event Action<GitChangedFile>? FileSelected;

    public IInputElement FocusTarget => _list.Focus;

    public bool IsChangesOpen => _changesOpen;

    /// <summary>The commit message is kept across openings (cleared only after a successful commit); the changes panel closes.</summary>
    public void Reset()
    {
        _list.ClearSearch();
        SetMessage(null);
        ClearResult();
        SetChangesOpen(false);
    }

    /// <summary>A different project is shown: nothing typed or counted for the old one may stay.</summary>
    public void ResetForProject()
    {
        ClearCommitMessage();
        SetStatus(null);
        ClearResult();
        SetChangesOpen(false);
        ChangeList.ItemsSource = null;
    }

    /// <summary>Inline success line ("✓ Pulled"), cleared by itself after a few seconds.</summary>
    public void ShowResult(string text)
    {
        ClearResult();
        ResultText.Text = "✓ " + text;
        ResultText.Visibility = Visibility.Visible;
        _resultTimer.Start();
    }

    /// <summary>Inline message with an optional button (e.g. "Pulled. Hot restart?"); stays until the next action.</summary>
    public void ShowAdvice(string text, string? actionLabel, Action? action)
    {
        ClearResult();
        AdviceText.Text = text;
        _adviceAction = action;
        AdviceButton.Content = actionLabel;
        AdviceButton.Visibility = actionLabel is null ? Visibility.Collapsed : Visibility.Visible;
        AdviceRow.Visibility = Visibility.Visible;
    }

    public void ClearResult()
    {
        _resultTimer.Stop();
        ResultText.Visibility = Visibility.Collapsed;
        AdviceRow.Visibility = Visibility.Collapsed;
        _adviceAction = null;
    }

    /// <summary>Branch list loading.</summary>
    public void SetBusy(bool busy)
    {
        _loading = busy;
        UpdateEnabled();
    }

    /// <summary>A git action (fetch, pull, commit, push) is running: buttons are disabled and the spinner shows.</summary>
    public void SetOperationBusy(bool busy)
    {
        _operating = busy;
        List.IsEnabled = !busy; // no branch switch while fetch / pull / push runs
        UpdateEnabled();
    }

    /// <summary>A changed-file list or diff load started (true) or ended (false); calls are paired.</summary>
    public void SetChangesBusy(bool busy)
    {
        _loadingChanges = Math.Max(0, _loadingChanges + (busy ? 1 : -1));
        UpdateEnabled();
    }

    public void ClearCommitMessage() => CommitMessage.Clear();

    /// <summary>Changed files and behind / ahead counts; null (status unreadable) hides them. No changes left closes the panel.</summary>
    public void SetStatus(GitStatusInfo? status)
    {
        _status = status;
        ChangedText.Text = status is null ? "" : status.ChangedFiles == 1 ? "1 changed file" : $"{status.ChangedFiles} changed files";
        PullButton.Content = status is { Behind: > 0 } ? $"Pull ↓{status.Behind}" : "Pull";
        if (_changesOpen && status is { ChangedFiles: 0 })
        {
            SetChangesOpen(false); // e.g. everything was just committed
        }
        UpdateEnabled();
    }

    public void SetBranches(IReadOnlyList<string> branches, string? current) =>
        _list.SetItems([.. branches.Select(b => new FlyoutItem { Title = b, IsCurrent = b == current, Value = b })]);

    /// <summary>Inline problem text (e.g. git failed), or null to hide it.</summary>
    public void SetMessage(string? text)
    {
        Message.Text = text ?? "";
        Message.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Fills the changed-file list, keeping the selected path when it is still there (else the first file), and raises
    /// <see cref="FileSelected"/> for it so its diff is (re)loaded.
    /// </summary>
    public void SetChanges(IReadOnlyList<GitChangedFile> files)
    {
        if (!_changesOpen)
        {
            return;
        }
        var keep = (ChangeList.SelectedItem as GitChangedFile)?.Path;
        var select = files.FirstOrDefault(f => f.Path == keep) ?? files.FirstOrDefault();
        _fillingChanges = true;
        try
        {
            ChangeList.ItemsSource = files;
            ChangeList.SelectedItem = select;
        }
        finally
        {
            _fillingChanges = false;
        }
        SetChangesMessage(files.Count == 0 ? "No uncommitted changes." : null);
        if (select is null)
        {
            ClearDiff();
            return;
        }
        ChangeList.ScrollIntoView(select);
        FileSelected?.Invoke(select);
    }

    /// <summary>The changed files could not be read.</summary>
    public void SetChangesError(string text)
    {
        ChangeList.ItemsSource = null;
        SetChangesMessage(text);
        ClearDiff();
    }

    public void ShowDiff(GitChangedFile file, IReadOnlyList<DiffLine> rows)
    {
        SetDiffTitle(file);
        DiffMessage.Visibility = Visibility.Collapsed;
        DiffList.ItemsSource = rows;
        if (rows.Count > 0)
        {
            DiffList.ScrollIntoView(rows[0]);
        }
    }

    public void ShowDiffError(GitChangedFile file, string text)
    {
        SetDiffTitle(file);
        DiffList.ItemsSource = null;
        DiffMessage.Text = text;
        DiffMessage.Visibility = Visibility.Visible;
    }

    private void SetChangesOpen(bool open)
    {
        if (open == _changesOpen)
        {
            return;
        }
        _changesOpen = open;
        ChangesChevron.Text = open ? "" : ""; // chevron down / right
        AutomationProperties.SetName(ChangesButton, open ? "Hide changed files" : "Show changed files");
        ChangesSection.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        BranchSection.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        DiffPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        MainColumn.Width = open ? new GridLength(ChangesColumnWidth) : new GridLength(1, GridUnitType.Star);
        DiffColumn.Width = open ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (!open)
        {
            SetChangesMessage(null);
            ClearDiff();
        }
        ChangesToggled?.Invoke(open);
        UpdateEnabled();
    }

    private void SetChangesMessage(string? text)
    {
        ChangesMessage.Text = text ?? "";
        ChangesMessage.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetDiffTitle(GitChangedFile file)
    {
        var title = file is { Kind: GitChangeKind.Renamed or GitChangeKind.Copied, OldPath: { } old } ? $"{old} → {file.Path}" : file.Path;
        DiffTitle.Text = title;
        DiffTitle.ToolTip = title;
    }

    private void ClearDiff()
    {
        DiffList.ItemsSource = null;
        DiffTitle.Text = "";
        DiffTitle.ToolTip = null;
        DiffMessage.Visibility = Visibility.Collapsed;
    }

    private void RaiseCommit()
    {
        if (CommitButton.IsEnabled)
        {
            CommitRequested?.Invoke(CommitMessage.Text.Trim());
        }
    }

    private void UpdateEnabled()
    {
        Spinner.Visibility = _loading || _operating || _loadingChanges > 0 ? Visibility.Visible : Visibility.Collapsed;
        FetchButton.IsEnabled = !_operating;
        PullButton.IsEnabled = !_operating;
        // Push sends pending commits, or publishes a branch that has no upstream yet.
        var label = _status is { Ahead: > 0 } ahead ? $"Push ↑{ahead.Ahead}" : "Push";
        PushButton.Content = label;
        AutomationProperties.SetName(PushButton, label);
        PushButton.IsEnabled = !_operating && _status is { } status && (status.Ahead > 0 || status.Upstream is null);
        // An unreadable status (null) still allows a commit: git reports "Nothing to commit." itself.
        CommitButton.IsEnabled = !_operating && !string.IsNullOrWhiteSpace(CommitMessage.Text) && _status is not { ChangedFiles: 0 };
        ChangesButton.IsEnabled = _changesOpen || _status is { ChangedFiles: > 0 };
    }
}
