using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StatefulBar.Core.Git;

namespace StatefulBar.App.Flyouts;

internal sealed partial class BranchFlyout : UserControl
{
    private readonly FlyoutList _list;
    private bool _loading;
    private bool _operating;
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
        CommitPushButton.Click += (_, _) => RaiseCommitAndPush();
        CommitMessage.TextChanged += (_, _) => UpdateEnabled();
        CommitMessage.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                // Enter commits; pushing pending commits only is a deliberate button click.
                if (!string.IsNullOrWhiteSpace(CommitMessage.Text))
                {
                    RaiseCommitAndPush();
                }
                e.Handled = true;
            }
        };
    }

    public event Action<string>? BranchChosen;

    public event Action? FetchRequested;

    public event Action? PullRequested;

    public event Action<string>? CommitAndPushRequested;

    public event Action? PushRequested;

    public IInputElement FocusTarget => _list.Focus;

    /// <summary>The commit message is kept across openings (cleared only after a successful push).</summary>
    public void Reset()
    {
        _list.ClearSearch();
        SetMessage(null);
        ClearResult();
    }

    /// <summary>A different project is shown: nothing typed or counted for the old one may stay.</summary>
    public void ResetForProject()
    {
        ClearCommitMessage();
        SetStatus(null);
        ClearResult();
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
        _status = status;
        var changed = status is null ? "" : status.ChangedFiles == 1 ? "1 changed file" : $"{status.ChangedFiles} changed files";
        ChangedText.Text = status is { Ahead: > 0 } ? $"{changed} · ↑{status.Ahead}" : changed;
        PullButton.Content = status is { Behind: > 0 } ? $"Pull ↓{status.Behind}" : "Pull";
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

    private void RaiseCommitAndPush()
    {
        if (!CommitPushButton.IsEnabled)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(CommitMessage.Text))
        {
            PushRequested?.Invoke();
        }
        else
        {
            CommitAndPushRequested?.Invoke(CommitMessage.Text.Trim());
        }
    }

    private void UpdateEnabled()
    {
        Spinner.Visibility = _loading || _operating ? Visibility.Visible : Visibility.Collapsed;
        FetchButton.IsEnabled = !_operating;
        PullButton.IsEnabled = !_operating;
        // Without a message the button only pushes: pending commits, or a branch that has no upstream yet.
        var hasMessage = !string.IsNullOrWhiteSpace(CommitMessage.Text);
        var pushOnly = !hasMessage && _status is { } status && (status.Ahead > 0 || status.Upstream is null);
        var label = pushOnly ? (_status is { Ahead: > 0 } ahead ? $"Push ↑{ahead.Ahead}" : "Push") : "Commit and push";
        CommitPushButton.Content = label;
        AutomationProperties.SetName(CommitPushButton, label);
        CommitPushButton.IsEnabled = !_operating && (hasMessage || pushOnly);
    }
}
