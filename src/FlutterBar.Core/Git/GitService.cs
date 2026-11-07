using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Git;

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
        var result = await RunAsync(root, CommandTimeout, null, ct, "status", "--porcelain=v2", "--branch").ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git status failed (exit {result.ExitCode}): {FailureText(result)}");
        }
        return ParseStatus(result.Stdout);
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
    public Task<GitResult> PullAsync(string root, CancellationToken ct) =>
        NetworkAsync(root, "Pulled", ct, "pull");

    /// <summary>
    /// <c>git add -A</c>, <c>git commit -m</c>, then <c>git push</c> (<c>push -u origin &lt;branch&gt;</c> without an upstream).
    /// No pull beforehand and never <c>--force</c>; a rejected push says to pull first.
    /// </summary>
    public async Task<GitResult> CommitAndPushAsync(string root, string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new GitResult(false, "Enter a commit message");
        }
        var branch = await GetSymbolicBranchAsync(root).ConfigureAwait(false);
        if (branch is null)
        {
            return new GitResult(false, "HEAD is detached. Switch to a branch first");
        }

        var add = await RunAsync(root, CommandTimeout, null, ct, "add", "-A").ConfigureAwait(false);
        if (add.ExitCode != 0)
        {
            return new GitResult(false, FailureText(add));
        }
        // Exit 1 = staged changes exist, 0 = nothing staged (so nothing to commit).
        var staged = await RunAsync(root, CommandTimeout, null, ct, "diff", "--cached", "--quiet").ConfigureAwait(false);
        if (staged.ExitCode == 0)
        {
            return new GitResult(false, "Nothing to commit");
        }
        if (staged.ExitCode != 1)
        {
            return new GitResult(false, FailureText(staged));
        }
        var commit = await RunAsync(root, CommandTimeout, null, ct, "commit", "-m", message).ConfigureAwait(false);
        if (commit.ExitCode != 0)
        {
            return new GitResult(false, FailureText(commit));
        }

        var upstream = await RunAsync(root, CommandTimeout, null, ct, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}")
            .ConfigureAwait(false);
        string[] pushArgs = upstream.ExitCode == 0 ? ["push"] : ["push", "-u", "origin", branch];
        var push = await RunAsync(root, NetworkTimeout, NoPromptEnvironment, ct, pushArgs).ConfigureAwait(false);
        if (push.ExitCode == 0)
        {
            return new GitResult(true, $"Pushed to {branch}");
        }
        var text = FailureText(push);
        var rejected = text.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || text.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || text.Contains("fetch first", StringComparison.OrdinalIgnoreCase);
        return new GitResult(false, rejected ? $"{text}\nPull first" : text);
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
        var text = FailureText(result);
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
        string[] full = ["-c", "core.quotepath=off", .. args];
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

    private static string FailureText(ProcessResult result)
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
        return result.ExitCode == -1 ? "git timed out" : $"git exited with code {result.ExitCode}";
    }
}
