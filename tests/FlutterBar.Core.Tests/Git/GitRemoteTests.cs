using FlutterBar.Core.Git;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Tests.Git;

/// <summary>Fetch, pull and commit-and-push against a real bare remote in a temp folder (spec §5.5).</summary>
public class GitRemoteTests
{
    // The working copy lives in Temp/work so the remote and the clones next to it are not inside its worktree.
    private static async Task<(GitTestRepo Repo, string Remote)> SetupAsync()
    {
        var repo = await GitTestRepo.CreateAsync("work");
        var remote = await repo.AddBareRemoteAsync();
        return (repo, remote);
    }

    [GitFact]
    public async Task Status_CountsChangesAheadBehind()
    {
        var (repo, remote) = await SetupAsync();
        using var _ = repo;
        var other = await repo.CloneAsync(remote);
        await repo.PushFromAsync(other, "remote.txt", "r\n", "remote commit");
        repo.Write("local.txt", "l\n");
        await repo.CommitAllAsync("local commit");
        await repo.GitAsync("fetch");
        repo.Write("pubspec.yaml", "name: app2\n");
        repo.Write("new.txt", "n\n");

        var status = await repo.Service.GetStatusAsync(repo.Root, CancellationToken.None);

        Assert.Equal(new GitStatusInfo(2, 1, 1, "origin/main"), status);
    }

    [GitFact]
    public async Task Status_NoUpstream_ZeroAheadBehindAndNullUpstream()
    {
        using var repo = await GitTestRepo.CreateAsync();
        var status = await repo.Service.GetStatusAsync(repo.Root, CancellationToken.None);
        Assert.Equal(new GitStatusInfo(0, 0, 0, null), status);
    }

    [GitFact]
    public async Task Fetch_Prune_Ok()
    {
        var (repo, remote) = await SetupAsync();
        using var _ = repo;
        var other = await repo.CloneAsync(remote);
        await repo.GitInAsync(other, "push", "origin", "HEAD:refs/heads/feature");

        var fetched = await repo.Service.FetchAsync(repo.Root, CancellationToken.None);
        Assert.True(fetched.Success, fetched.Message);
        Assert.Contains("origin/feature", await repo.GitAsync("branch", "-r"));

        await repo.GitInAsync(other, "push", "origin", "--delete", "feature");
        Assert.True((await repo.Service.FetchAsync(repo.Root, CancellationToken.None)).Success);
        Assert.DoesNotContain("origin/feature", await repo.GitAsync("branch", "-r"));
    }

    [GitFact]
    public async Task Fetch_BadRemote_ReturnsGitStderr()
    {
        using var repo = await GitTestRepo.CreateAsync("work");
        await repo.GitAsync("remote", "add", "origin", Path.Combine(repo.Temp, "missing.git"));
        var result = await repo.Service.FetchAsync(repo.Root, CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [GitFact]
    public async Task Pull_FastForward_UpdatesWorkingTree()
    {
        var (repo, remote) = await SetupAsync();
        using var _ = repo;
        var other = await repo.CloneAsync(remote);
        await repo.PushFromAsync(other, "lib/added.dart", "// added\n", "add file");

        var result = await repo.Service.PullAsync(repo.Root, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(repo.Root, "lib", "added.dart")));
    }

    [GitFact]
    public async Task Pull_Conflict_ReturnsGitStderr()
    {
        var (repo, remote) = await SetupAsync();
        using var _ = repo;
        await repo.GitAsync("config", "pull.rebase", "false"); // deterministic merge, independent of the user's global config
        var other = await repo.CloneAsync(remote);
        await repo.PushFromAsync(other, "lib/main.dart", "void main() { remote(); }\n", "remote edit");
        repo.Write("lib/main.dart", "void main() { local(); }\n");
        await repo.CommitAllAsync("local edit");

        var result = await repo.Service.PullAsync(repo.Root, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("conflict", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [GitFact]
    public async Task CommitAndPush_StagesAllAndPushes()
    {
        var (repo, remote) = await SetupAsync();
        using var _ = repo;
        repo.Write("pubspec.yaml", "name: app2\n");
        repo.Write("lib/new.dart", "// new\n"); // untracked: git add -A must include it

        var result = await repo.Service.CommitAndPushAsync(repo.Root, "feat: wip", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Pushed to main", result.Message);
        Assert.Equal("feat: wip", await repo.GitInAsync(repo.Temp, "--git-dir", remote, "log", "-1", "--format=%s", "main"));
        Assert.Contains("lib/new.dart", await repo.GitInAsync(repo.Temp, "--git-dir", remote, "ls-tree", "-r", "--name-only", "main"));
        Assert.Equal(0, (await repo.Service.GetStatusAsync(repo.Root, CancellationToken.None)).ChangedFiles);
    }

    [GitFact]
    public async Task CommitAndPush_NoUpstream_SetsUpstream()
    {
        var (repo, remote) = await SetupAsync();
        using var _ = repo;
        await repo.GitAsync("switch", "-c", "feature/x");
        repo.Write("lib/x.dart", "// x\n");

        var result = await repo.Service.CommitAndPushAsync(repo.Root, "add x", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Pushed to feature/x", result.Message);
        Assert.Equal("add x", await repo.GitInAsync(repo.Temp, "--git-dir", remote, "log", "-1", "--format=%s", "feature/x"));
        Assert.Equal("origin/feature/x", await repo.GitAsync("rev-parse", "--abbrev-ref", "feature/x@{u}"));
    }

    [GitFact]
    public async Task CommitAndPush_EmptyMessage_Rejected()
    {
        var (repo, remote) = await SetupAsync();
        using var _ = repo;
        repo.Write("lib/new.dart", "// new\n");
        var head = await repo.GitAsync("rev-parse", "HEAD");

        var result = await repo.Service.CommitAndPushAsync(repo.Root, "   ", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(head, await repo.GitAsync("rev-parse", "HEAD"));
        Assert.Equal(head, await repo.GitInAsync(repo.Temp, "--git-dir", remote, "rev-parse", "main"));
    }

    [GitFact]
    public async Task CommitAndPush_NothingToCommit_ReturnsMessage()
    {
        var (repo, _) = await SetupAsync();
        using var r = repo;

        var result = await repo.Service.CommitAndPushAsync(repo.Root, "msg", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Nothing to commit", result.Message);
    }

    [GitFact]
    public async Task Push_Rejected_NonFastForward_SuggestsPull()
    {
        var (repo, remote) = await SetupAsync();
        using var _ = repo;
        var other = await repo.CloneAsync(remote);
        await repo.PushFromAsync(other, "remote.txt", "r\n", "remote commit");
        var remoteHead = await repo.GitInAsync(repo.Temp, "--git-dir", remote, "rev-parse", "main");
        repo.Write("local.txt", "l\n");

        var result = await repo.Service.CommitAndPushAsync(repo.Root, "local commit", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Pull first", result.Message);
        Assert.Equal(remoteHead, await repo.GitInAsync(repo.Temp, "--git-dir", remote, "rev-parse", "main")); // never forced
    }

    [GitFact]
    public async Task CommitAndPush_DetachedHead_Rejected()
    {
        var (repo, _) = await SetupAsync();
        using var r = repo;
        await repo.GitAsync("checkout", "--detach");
        repo.Write("lib/new.dart", "// new\n");

        var result = await repo.Service.CommitAndPushAsync(repo.Root, "msg", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("detached", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetworkCommands_UseNoTerminalPrompt_TwoMinuteTimeout_NeverForce()
    {
        var runner = new RecordingRunner(spec => spec.Args.Contains("diff")
            ? new ProcessResult(1, "", "") // staged changes exist
            : spec.Args.Contains("symbolic-ref") ? new ProcessResult(0, "main\n", "") : new ProcessResult(0, "", ""));
        var git = new GitService(runner, "git");

        await git.FetchAsync("C:\\p", CancellationToken.None);
        await git.PullAsync("C:\\p", CancellationToken.None);
        await git.CommitAndPushAsync("C:\\p", "msg", CancellationToken.None);

        var network = runner.Calls.Where(c => c.Spec.Args.Any(a => a is "fetch" or "pull" or "push")).ToList();
        Assert.Equal(3, network.Count);
        foreach (var (spec, timeout) in network)
        {
            Assert.Equal("0", spec.Environment?["GIT_TERMINAL_PROMPT"]);
            Assert.Equal(TimeSpan.FromMinutes(2), timeout);
        }
        Assert.DoesNotContain(runner.Calls, c => c.Spec.Args.Any(a => a is "--force" or "-f" or "--force-with-lease"));
    }

    [GitFact]
    public async Task BranchFlow_Pull_AdvisesByPubspecChange_OnlyWhenSessionLive()
    {
        var (repo, remote) = await SetupAsync();
        using var _ = repo;
        var other = await repo.CloneAsync(remote);

        await repo.PushFromAsync(other, "lib/a.dart", "// a\n", "dart only");
        var hot = await BranchFlow.PullAsync(repo.Service, repo.Root, sessionActive: true);
        Assert.True(hot.Success, hot.Message);
        Assert.Equal(BranchChangeAdvice.HotRestart, hot.Advice);

        await repo.PushFromAsync(other, "pubspec.yaml", "name: app\nversion: 2\n", "pubspec");
        var rerun = await BranchFlow.PullAsync(repo.Service, repo.Root, sessionActive: true);
        Assert.Equal(BranchChangeAdvice.RerunRequired, rerun.Advice);

        await repo.PushFromAsync(other, "lib/b.dart", "// b\n", "more");
        var idle = await BranchFlow.PullAsync(repo.Service, repo.Root, sessionActive: false);
        Assert.True(idle.Success);
        Assert.Null(idle.Advice);

        var upToDate = await BranchFlow.PullAsync(repo.Service, repo.Root, sessionActive: true);
        Assert.True(upToDate.Success);
        Assert.Null(upToDate.Advice); // HEAD did not move
    }

    private sealed class RecordingRunner(Func<ProcessSpec, ProcessResult> handler) : IProcessRunner
    {
        private readonly List<(ProcessSpec Spec, TimeSpan Timeout)> _calls = [];

        public IReadOnlyList<(ProcessSpec Spec, TimeSpan Timeout)> Calls => _calls;

        public Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct)
        {
            _calls.Add((spec, timeout));
            return Task.FromResult(handler(spec));
        }
    }
}
