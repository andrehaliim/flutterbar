using StatefulBar.Core.Processes;

namespace StatefulBar.Core.Git;

public sealed record GitResult(bool Success, string Message);

/// <summary>
/// <paramref name="ChangedFiles"/> counts every entry of <c>git status</c> (modified, staged, untracked, conflicted);
/// <paramref name="Upstream"/> is null without one, then ahead/behind are 0.
/// </summary>
public sealed record GitStatusInfo(int ChangedFiles, int Ahead, int Behind, string? Upstream);

public enum BranchChangeAdvice
{
    HotRestart,
    RerunRequired,
}

public sealed class GitService(IProcessRunner runner, string gitExe)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(2);

    // Network commands must never wait for a terminal prompt; a credential helper (Git Credential Manager) still works.
    private static readonly IReadOnlyDictionary<string, string> NoPromptEnvironment =
        new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };

    public async Task<bool> IsRepoAsync(string root)
    {
        // Contract: "not a repo" is a normal answer, so a non-zero exit means false (not an error).
        var result = await RunAsync(root, "rev-parse", "--is-inside-work-tree").ConfigureAwait(false);
        return result.ExitCode == 0 && result.Stdout.Trim() == "true";
    }

    public async Task<IReadOnlyList<string>> GetLocalBranchesAsync(string root)
    {
        var stdout = await QueryAsync(root, "branch", "--format=%(refname:short)").ConfigureAwait(false);
        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => !b.StartsWith('(')) // "(HEAD detached at ...)" is not a branch
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<string> GetCurrentAsync(string root)
    {
        var name = (await QueryAsync(root, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
        if (name != "HEAD")
        {
            return name;
        }
        return (await QueryAsync(root, "rev-parse", "--short", "HEAD").ConfigureAwait(false)).Trim();
    }

    /// <summary>
    /// Branch HEAD points to via <c>git symbolic-ref --short HEAD</c>, also when it has no commit yet (fresh repo, where
    /// <see cref="GetCurrentAsync"/> throws). Null when HEAD is detached or git fails.
    /// </summary>
    public async Task<string?> GetSymbolicBranchAsync(string root)
    {
        var result = await RunAsync(root, "symbolic-ref", "--quiet", "--short", "HEAD").ConfigureAwait(false);
        var name = result.Stdout.Trim();
        return result.ExitCode == 0 && name.Length > 0 ? name : null;
    }

    public async Task<string> GetHeadCommitAsync(string root) =>
        (await QueryAsync(root, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

    public async Task<GitResult> SwitchAsync(string root, string branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-'))
        {
            return new GitResult(false, $"Invalid branch name: {branch}");
        }

        var result = await RunAsync(root, "switch", branch).ConfigureAwait(false);
        return result.ExitCode == 0
            ? new GitResult(true, $"Switched to branch {branch}")
            : new GitResult(false, FailureText(result));
    }

    public async Task<BranchChangeAdvice> AdviseAfterSwitchAsync(string root, string oldCommit, string newCommit)
    {
        var stdout = await QueryAsync(
            root, "diff", "--name-only", oldCommit, newCommit, "--", "pubspec.yaml", "pubspec.lock").ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stdout) ? BranchChangeAdvice.HotRestart : BranchChangeAdvice.RerunRequired;
    }

    public async Task<GitStatusInfo> GetStatusAsync(string root, CancellationToken ct)
    {
        var (status, _) = await ReadStatusAsync(root, ct).ConfigureAwait(false);
        return status;
    }

    private async Task<(GitStatusInfo Status, bool Unmerged)> ReadStatusAsync(string root, CancellationToken ct)
    {
        // --untracked-files=all: a new folder counts its files, not one entry.
        var result = await RunAsync(root, CommandTimeout, null, ct, "--no-optional-locks", "status", "--porcelain=v2", "--branch", "--untracked-files=all")
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git status failed (exit {result.ExitCode}): {FailureText(result)}");
        }
        var unmerged = result.Stdout.Split('\n').Any(l => l.StartsWith("u ", StringComparison.Ordinal));
        return (ParseStatus(result.Stdout), unmerged);
    }

    /// <summary>Unmerged paths, or a merge / rebase / cherry-pick still in progress.</summary>
    private async Task<bool> HasConflictedStateAsync(string root, CancellationToken ct)
    {
        var (_, unmerged) = await ReadStatusAsync(root, ct).ConfigureAwait(false);
        if (unmerged)
        {
            return true;
        }
        var paths = await RunAsync(
            root, CommandTimeout, null, ct, "rev-parse",
            "--git-path", "MERGE_HEAD", "--git-path", "CHERRY_PICK_HEAD", "--git-path", "REVERT_HEAD", "--git-path", "sequencer",
            "--git-path", "rebase-merge", "--git-path", "rebase-apply")
            .ConfigureAwait(false);
        if (paths.ExitCode != 0)
        {
            return false;
        }
        return paths.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => File.Exists(Path.GetFullPath(p, root)) || Directory.Exists(Path.GetFullPath(p, root)));
    }

    internal static GitStatusInfo ParseStatus(string porcelainV2)
    {
        int changed = 0, ahead = 0, behind = 0;
        string? upstream = null;
        foreach (var line in porcelainV2.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = line["# branch.upstream ".Length..];
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = line["# branch.ab ".Length..].Split(' ');
                if (parts.Length == 2
                    && int.TryParse(parts[0].TrimStart('+'), out var a)
                    && int.TryParse(parts[1].TrimStart('-'), out var b))
                {
                    (ahead, behind) = (a, b);
                }
            }
            else if (!line.StartsWith('#'))
            {
                changed++; // 1 ordinary, 2 renamed/copied, u unmerged, ? untracked
            }
        }
        return new GitStatusInfo(changed, ahead, behind, upstream);
    }

    public Task<GitResult> FetchAsync(string root, CancellationToken ct) =>
        NetworkAsync(root, "Fetched", ct, "fetch", "--prune");

    /// <summary>Plain <c>git pull</c>: merge or rebase follows the user's git config.</summary>
    public async Task<GitResult> PullAsync(string root, CancellationToken ct)
    {
        if (await HasConflictedStateAsync(root, ct).ConfigureAwait(false))
        {
            return new GitResult(false, ResolveConflictsFirst);
        }
        return await NetworkAsync(root, "Pulled", ct, "pull").ConfigureAwait(false);
    }

    public const string ResolveConflictsFirst = "Resolve conflicts first.";

    public const string UpstreamGone = "The upstream branch is gone. Push from a terminal if you want to recreate it.";

    /// <summary>Start of the failure text when the commit exists locally but the push failed (the UI then clears the message).</summary>
    public const string CommittedLocallyPrefix = "Committed locally. Push failed: ";

    /// <summary>
    /// <c>git push</c> only (<c>push -u origin &lt;branch&gt;</c> without an upstream), for commits an earlier push left behind.
    /// Never <c>--force</c>; a rejected push says to pull first. Nothing is staged or committed.
    /// </summary>
    public async Task<GitResult> PushAsync(string root, CancellationToken ct)
    {
        var (target, error) = await PreparePushAsync(root, ct).ConfigureAwait(false);
        if (error is not null || target is null)
        {
            return error ?? new GitResult(false, "Nothing to push.");
        }
        var (status, _) = await ReadStatusAsync(root, ct).ConfigureAwait(false);
        if (target.HasUpstream && status.Ahead == 0)
        {
            return new GitResult(false, "Nothing to push.");
        }
        return await PushCoreAsync(root, target, committed: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>git add -A</c>, <c>git commit -m</c>, then <c>git push</c> (<c>push -u origin &lt;branch&gt;</c> without an upstream).
    /// No pull beforehand and never <c>--force</c>; a rejected push says to pull first. The message is only required when
    /// there is something to commit; without one, pending commits are pushed.
    /// </summary>
    public async Task<GitResult> CommitAndPushAsync(string root, string message, CancellationToken ct)
    {
        var (target, error) = await PreparePushAsync(root, ct).ConfigureAwait(false);
        if (error is not null || target is null)
        {
            return error ?? new GitResult(false, "Nothing to commit.");
        }

        var committed = false;
        if (string.IsNullOrWhiteSpace(message))
        {
            var (pending, _) = await ReadStatusAsync(root, ct).ConfigureAwait(false);
            if (pending.ChangedFiles > 0)
            {
                return new GitResult(false, "Enter a commit message.");
            }
        }
        else
        {
            var add = await RunAsync(root, CommandTimeout, null, ct, "add", "-A").ConfigureAwait(false);
            if (add.ExitCode != 0)
            {
                return new GitResult(false, FailureText(add));
            }
            // Exit 1 = staged changes exist, 0 = nothing staged.
            var staged = await RunAsync(root, CommandTimeout, null, ct, "diff", "--cached", "--quiet").ConfigureAwait(false);
            if (staged.ExitCode != 0 && staged.ExitCode != 1)
            {
                return new GitResult(false, FailureText(staged));
            }
            if (staged.ExitCode == 1)
            {
                // Hooks (lefthook, husky) and GPG pinentry can be slow: same limit as the network commands.
                var commit = await RunAsync(root, NetworkTimeout, NoPromptEnvironment, ct, "commit", "-m", message).ConfigureAwait(false);
                if (commit.ExitCode != 0)
                {
                    return new GitResult(false, FailureText(commit));
                }
                committed = true;
            }
        }

        if (!committed)
        {
            // Nothing new to commit: still push commits an earlier failed push left behind.
            var (status, _) = await ReadStatusAsync(root, ct).ConfigureAwait(false);
            if (target.HasUpstream && status.Ahead == 0)
            {
                return new GitResult(false, "Nothing to commit.");
            }
        }
        return await PushCoreAsync(root, target, committed, ct).ConfigureAwait(false);
    }

    private sealed record PushTarget(string Branch, bool HasUpstream);

    /// <summary>Checks shared by push and commit-and-push, all before anything is staged.</summary>
    private async Task<(PushTarget? Target, GitResult? Error)> PreparePushAsync(string root, CancellationToken ct)
    {
        var branch = await GetSymbolicBranchAsync(root).ConfigureAwait(false);
        if (branch is null)
        {
            return (null, new GitResult(false, "HEAD is detached. Switch to a branch first."));
        }
        if (await HasConflictedStateAsync(root, ct).ConfigureAwait(false))
        {
            return (null, new GitResult(false, ResolveConflictsFirst)); // add -A would mark conflict markers as resolved
        }
        var upstream = await RunAsync(root, CommandTimeout, null, ct, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}")
            .ConfigureAwait(false);
        var hasUpstream = upstream.ExitCode == 0;
        if (!hasUpstream)
        {
            var configured = await RunAsync(root, CommandTimeout, null, ct, "config", "--get", $"branch.{branch}.merge").ConfigureAwait(false);
            if (configured.ExitCode == 0 && configured.Stdout.Trim().Length > 0)
            {
                return (null, new GitResult(false, UpstreamGone));
            }
        }
        return (new PushTarget(branch, hasUpstream), null);
    }

    private async Task<GitResult> PushCoreAsync(string root, PushTarget target, bool committed, CancellationToken ct)
    {
        string[] pushArgs = target.HasUpstream ? ["push"] : ["push", "-u", "origin", target.Branch];
        var push = await RunAsync(root, NetworkTimeout, NoPromptEnvironment, ct, pushArgs).ConfigureAwait(false);
        if (push.ExitCode == 0)
        {
            return new GitResult(true, $"Pushed to {target.Branch}");
        }
        var text = FailureText(push, network: true);
        // "[rejected]" (non-fast-forward / fetch first) is fixed by pulling; "[remote rejected]" (hook, protected branch) is not.
        var needsPull = text.Contains("[rejected]", StringComparison.Ordinal)
            && (text.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
                || text.Contains("fetch first", StringComparison.OrdinalIgnoreCase));
        if (needsPull)
        {
            text += "\nPull first.";
        }
        return new GitResult(false, committed ? CommittedLocallyPrefix + text : text);
    }

    private async Task<GitResult> NetworkAsync(string root, string successMessage, CancellationToken ct, params string[] args)
    {
        var result = await RunAsync(root, NetworkTimeout, NoPromptEnvironment, ct, args).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return new GitResult(true, successMessage);
        }
        // A failed merge reports its conflicts on stdout and only the fetch progress on stderr: keep both.
        var stdout = result.Stdout.Trim();
        var text = FailureText(result, network: true);
        return new GitResult(false, stdout.Length > 0 && !text.Contains(stdout, StringComparison.Ordinal) ? $"{text}\n{stdout}" : text);
    }

    public async Task<string> GetHeadFilePathAsync(string root)
    {
        var path = (await QueryAsync(root, "rev-parse", "--git-path", "HEAD").ConfigureAwait(false)).Trim();
        // Relative output is relative to the working directory we ran in (root); worktrees give absolute paths.
        return Path.GetFullPath(path, root);
    }

    private Task<ProcessResult> RunAsync(string root, params string[] args) =>
        RunAsync(root, CommandTimeout, null, CancellationToken.None, args);

    private Task<ProcessResult> RunAsync(
        string root, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment, CancellationToken ct, params string[] args)
    {
        // quotepath=off keeps non-ASCII branch names / paths unescaped in the (UTF-8) output.
        // The advice settings drop git's multi-line "hint:" blocks, which would only fill the toast.
        string[] full =
        [
            "-c", "core.quotepath=off",
            "-c", "advice.pushUpdateRejected=false", "-c", "advice.pushNonFFCurrent=false", "-c", "advice.pushFetchFirst=false",
            "-c", "advice.statusHints=false",
            .. args,
        ];
        return runner.RunAsync(new ProcessSpec(gitExe, full, root, environment), timeout, ct);
    }

    private async Task<string> QueryAsync(string root, params string[] args)
    {
        var result = await RunAsync(root, args).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {args[0]} failed (exit {result.ExitCode}): {FailureText(result)}");
        }
        return result.Stdout;
    }

    private static string FailureText(ProcessResult result, bool network = false)
    {
        var text = result.Stderr.Trim();
        if (text.Length > 0)
        {
            return text;
        }
        // A merge conflict reports on stdout only.
        text = result.Stdout.Trim();
        if (text.Length > 0 && result.ExitCode != -1)
        {
            return text;
        }
        if (result.ExitCode == -1)
        {
            // A hidden console cannot show an ssh passphrase or host-key prompt, so a network command just hangs until the limit.
            return network ? "git timed out (waiting for credentials or SSH passphrase?)" : "git timed out";
        }
        return $"git exited with code {result.ExitCode}";
    }
}
