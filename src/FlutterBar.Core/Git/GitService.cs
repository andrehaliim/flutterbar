using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Git;

public sealed record GitResult(bool Success, string Message);

public enum BranchChangeAdvice
{
    HotRestart,
    RerunRequired,
}

public sealed class GitService(IProcessRunner runner, string gitExe)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

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

    public async Task<string> GetHeadFilePathAsync(string root)
    {
        var path = (await QueryAsync(root, "rev-parse", "--git-path", "HEAD").ConfigureAwait(false)).Trim();
        // Relative output is relative to the working directory we ran in (root); worktrees give absolute paths.
        return Path.GetFullPath(path, root);
    }

    private Task<ProcessResult> RunAsync(string root, params string[] args)
    {
        // quotepath=off keeps non-ASCII branch names / paths unescaped in the (UTF-8) output.
        string[] full = ["-c", "core.quotepath=off", .. args];
        return runner.RunAsync(new ProcessSpec(gitExe, full, root), CommandTimeout, CancellationToken.None);
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
        return result.ExitCode == -1 ? "git timed out" : $"git exited with code {result.ExitCode}";
    }
}
