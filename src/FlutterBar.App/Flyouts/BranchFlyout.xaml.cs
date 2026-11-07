using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlutterBar.Core.Git;

namespace FlutterBar.App.Flyouts;

internal sealed partial class BranchFlyout : UserControl
{
    private readonly FlyoutList _list;
    private bool _loading;
    private bool _operating;

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
        FetchButton.Click += (_, _) => FetchRequested?.Invoke();
        PullButton.Click += (_, _) => PullRequested?.Invoke();
        CommitPushButton.Click += (_, _) => RaiseCommitAndPush();
        CommitMessage.TextChanged += (_, _) => UpdateEnabled();
        CommitMessage.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RaiseCommitAndPush();
                e.Handled = true;
            }
        };
    }

    public event Action<string>? BranchChosen;

    public event Action? FetchRequested;

    public event Action? PullRequested;

    public event Action<string>? CommitAndPushRequested;

    public IInputElement FocusTarget => _list.Focus;

    /// <summary>The commit message is kept across openings (cleared only after a successful push).</summary>
    public void Reset()
    {
        _list.ClearSearch();
        SetMessage(null);
    }

    /// <summary>Branch list loading.</summary>
    public void SetBusy(bool busy)
    {
        _loading = busy;
        UpdateEnabled();
    }

    /// <summary>A git action (fetch, pull, commit and push) is running: buttons are disabled and the spinner shows.</summary>
    public void SetOperationBusy(bool busy)
    {
        _operating = busy;
        List.IsEnabled = !busy; // no branch switch while fetch / pull / push runs
        UpdateEnabled();
    }

    public void ClearCommitMessage() => CommitMessage.Clear();

    /// <summary>Changed files and behind count; null (status unreadable) hides them.</summary>
    public void SetStatus(GitStatusInfo? status)
    {
        ChangedText.Text = status is null ? "" : status.ChangedFiles == 1 ? "1 changed file" : $"{status.ChangedFiles} changed files";
        PullButton.Content = status is { Behind: > 0 } ? $"Pull ↓{status.Behind}" : "Pull";
    }

    public void SetBranches(IReadOnlyList<string> branches, string? current) =>
        _list.SetItems([.. branches.Select(b => new FlyoutItem { Title = b, IsCurrent = b == current, Value = b })]);

    /// <summary>Inline problem text (e.g. git failed), or null to hide it.</summary>
    public void SetMessage(string? text)
    {
        Message.Text = text ?? "";
        Message.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RaiseCommitAndPush()
    {
        if (CommitPushButton.IsEnabled)
        {
            CommitAndPushRequested?.Invoke(CommitMessage.Text.Trim());
        }
    }

    private void UpdateEnabled()
    {
        Spinner.Visibility = _loading || _operating ? Visibility.Visible : Visibility.Collapsed;
        FetchButton.IsEnabled = !_operating;
        PullButton.IsEnabled = !_operating;
        CommitPushButton.IsEnabled = !_operating && !string.IsNullOrWhiteSpace(CommitMessage.Text);
    }
}
