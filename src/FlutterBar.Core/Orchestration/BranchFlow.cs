using FlutterBar.Core.Git;

namespace FlutterBar.Core.Orchestration;

/// <summary>Branch chip state. <see cref="IsRepo"/> false hides the chip; <see cref="Error"/> is set when git failed.</summary>
public sealed record GitState(bool IsRepo, string? Branch, bool Unborn, string? Error)
{
    public string ToolTip => Error is not null
        ? $"Couldn't read the branch: {Error}"
        : Unborn ? $"Branch {Branch} {UiText.NoCommitYet}" : $"Branch {Branch}";
}

/// <summary><paramref name="Advice"/> is set only when a live session was affected and both commits are known.</summary>
public sealed record BranchSwitchOutcome(bool Success, string Message, BranchChangeAdvice? Advice, Exception? AdviceError = null);

/// <summary>Branch chip reading and switching (spec §5.5, brief Step 3).</summary>
public static class BranchFlow
{
    /// <summary>
    /// Reads the current branch. A fresh repo without commits makes <see cref="GitService.GetCurrentAsync"/> throw; then
    /// <c>git symbolic-ref</c> names the unborn branch. If that fails too, the original error is returned.
    /// </summary>
    public static async Task<GitState> ReadAsync(GitService git, string root)
    {
        ArgumentNullException.ThrowIfNull(git);
        if (!await git.IsRepoAsync(root).ConfigureAwait(false))
        {
            return new GitState(false, null, false, null);
        }
        try
        {
            return new GitState(true, await git.GetCurrentAsync(root).ConfigureAwait(false), false, null);
        }
        catch (InvalidOperationException ex)
        {
            var unborn = await git.GetSymbolicBranchAsync(root).ConfigureAwait(false);
            return unborn is not null
                ? new GitState(true, unborn, true, null)
                : new GitState(true, null, false, ex.Message);
        }
    }

    /// <summary><c>git switch</c>; for a live session also compares pubspec between the old and new HEAD.</summary>
    public static async Task<BranchSwitchOutcome> SwitchAsync(GitService git, string root, string branch, bool sessionActive)
    {
        ArgumentNullException.ThrowIfNull(git);
        string? oldCommit = null;
        Exception? adviceError = null;
        if (sessionActive)
        {
            try
            {
                oldCommit = await git.GetHeadCommitAsync(root).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                adviceError = ex; // unborn HEAD: nothing to compare, the switch itself still runs
            }
        }

        var result = await git.SwitchAsync(root, branch).ConfigureAwait(false);
        if (!result.Success)
        {
            return new BranchSwitchOutcome(false, result.Message, null);
        }
        if (oldCommit is null)
        {
            return new BranchSwitchOutcome(true, result.Message, null, adviceError);
        }
        try
        {
            var newCommit = await git.GetHeadCommitAsync(root).ConfigureAwait(false);
            var advice = await git.AdviseAfterSwitchAsync(root, oldCommit, newCommit).ConfigureAwait(false);
            return new BranchSwitchOutcome(true, result.Message, advice);
        }
        catch (InvalidOperationException ex)
        {
            return new BranchSwitchOutcome(true, result.Message, null, ex);
        }
    }

    /// <summary>
    /// <c>git pull</c>; for a live session whose HEAD moved, also compares pubspec between the old and new HEAD (same
    /// advice as a branch switch). Advice stays null when HEAD did not move.
    /// </summary>
    public static async Task<BranchSwitchOutcome> PullAsync(GitService git, string root, bool sessionActive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        string? oldCommit = null;
        Exception? adviceError = null;
        if (sessionActive)
        {
            try
            {
                oldCommit = await git.GetHeadCommitAsync(root).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                adviceError = ex;
            }
        }

        var result = await git.PullAsync(root, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return new BranchSwitchOutcome(false, result.Message, null);
        }
        if (oldCommit is null)
        {
            return new BranchSwitchOutcome(true, result.Message, null, adviceError);
        }
        try
        {
            var newCommit = await git.GetHeadCommitAsync(root).ConfigureAwait(false);
            if (newCommit == oldCommit)
            {
                return new BranchSwitchOutcome(true, result.Message, null);
            }
            var advice = await git.AdviseAfterSwitchAsync(root, oldCommit, newCommit).ConfigureAwait(false);
            return new BranchSwitchOutcome(true, result.Message, advice);
        }
        catch (InvalidOperationException ex)
        {
            return new BranchSwitchOutcome(true, result.Message, null, ex);
        }
    }

    /// <summary>Toast text and optional action label for <paramref name="advice"/>.</summary>
    public static (string Text, string? ActionLabel) AdviceToast(BranchChangeAdvice advice) => advice switch
    {
        BranchChangeAdvice.HotRestart => (UiText.BranchHotRestart, UiText.HotRestart),
        _ => (UiText.PubspecChanged, null),
    };
}
