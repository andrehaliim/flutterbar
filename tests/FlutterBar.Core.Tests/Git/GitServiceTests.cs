using System.Text.RegularExpressions;
using FlutterBar.Core.Git;
using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Tests.Git;

/// <summary>xunit v2 has no dynamic skip, so skip at discovery time when git is not installed.</summary>
public sealed class GitFactAttribute : FactAttribute
{
    public GitFactAttribute()
    {
        if (GitTestRepo.GitExe is null)
        {
            Skip = "git was not found on PATH";
        }
    }
}

internal sealed class GitTestRepo : IDisposable
{
    public static string? GitExe { get; } = new PathResolver().Find("git");

    private readonly ProcessRunner _runner = new();

    public GitTestRepo(string? dirName = null)
    {
        var temp = Directory.CreateTempSubdirectory("flutterbar-git-");
        Temp = temp.FullName;
        Root = dirName is null ? Temp : Directory.CreateDirectory(Path.Combine(Temp, dirName)).FullName;
        Service = new GitService(_runner, GitExe!);
    }

    public string Temp { get; }

    public string Root { get; }

    public GitService Service { get; }

    public static async Task<GitTestRepo> CreateAsync(string? dirName = null)
    {
        var repo = new GitTestRepo(dirName);
        await repo.GitAsync("init", "-b", "main");
        // Local-only config: never touch the global git config.
        await repo.GitAsync("config", "user.name", "Test");
        await repo.GitAsync("config", "user.email", "test@example.com");
        await repo.GitAsync("config", "commit.gpgsign", "false");
        repo.Write("lib/main.dart", "void main() {}\n");
        repo.Write("pubspec.yaml", "name: app\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-m", "init");
        return repo;
    }

    public void Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public async Task<string> GitAsync(params string[] args)
    {
        var result = await _runner.RunAsync(new ProcessSpec(GitExe!, args, Root), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)} failed: {result.Stderr}");
        return result.Stdout.Trim();
    }

    public async Task CommitAllAsync(string message)
    {
        await GitAsync("add", "-A");
        await GitAsync("commit", "-m", message);
    }

    public void Dispose()
    {
        try
        {
            // git marks object files read-only on Windows.
            foreach (var f in new DirectoryInfo(Temp).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                f.Attributes = FileAttributes.Normal;
            }
            Directory.Delete(Temp, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort temp cleanup
        }
    }
}

public class GitServiceTests
{
    [GitFact]
    public async Task NotARepo_IsRepoFalse()
    {
        using var repo = new GitTestRepo(); // no git init
        Assert.False(await repo.Service.IsRepoAsync(repo.Root));
    }

    [GitFact]
    public async Task IsRepo_True_ForRepo()
    {
        using var repo = await GitTestRepo.CreateAsync();
        Assert.True(await repo.Service.IsRepoAsync(repo.Root));
    }

    [GitFact]
    public async Task ListsLocalBranches_Sorted()
    {
        using var repo = await GitTestRepo.CreateAsync();
        await repo.GitAsync("branch", "feature/x");
        var branches = await repo.Service.GetLocalBranchesAsync(repo.Root);
        Assert.Equal(["feature/x", "main"], branches);
    }

    [GitFact]
    public async Task Current_OnBranch_ReturnsName()
    {
        using var repo = await GitTestRepo.CreateAsync();
        Assert.Equal("main", await repo.Service.GetCurrentAsync(repo.Root));
    }

    [GitFact]
    public async Task Current_Detached_ReturnsShortSha()
    {
        using var repo = await GitTestRepo.CreateAsync();
        await repo.GitAsync("checkout", "--detach");
        var current = await repo.Service.GetCurrentAsync(repo.Root);
        Assert.Matches(new Regex("^[0-9a-f]{7,12}$"), current);
    }

    [GitFact]
    public async Task Switch_Dirty_Conflict_ReturnsGitStderr()
    {
        using var repo = await GitTestRepo.CreateAsync();
        await repo.GitAsync("branch", "other");
        await repo.GitAsync("switch", "other");
        repo.Write("lib/main.dart", "void main() { print('other'); }\n");
        await repo.CommitAllAsync("change on other");
        await repo.GitAsync("switch", "main");
        repo.Write("lib/main.dart", "void main() { print('dirty'); }\n"); // uncommitted, conflicts with other

        var result = await repo.Service.SwitchAsync(repo.Root, "other");

        Assert.False(result.Success);
        Assert.Contains("would be overwritten", result.Message);
        Assert.Equal("main", await repo.Service.GetCurrentAsync(repo.Root));
    }

    [GitFact]
    public async Task Switch_Ok()
    {
        using var repo = await GitTestRepo.CreateAsync();
        await repo.GitAsync("branch", "feature/x");

        var result = await repo.Service.SwitchAsync(repo.Root, "feature/x");

        Assert.True(result.Success);
        Assert.Contains("feature/x", result.Message);
        Assert.Equal("feature/x", await repo.Service.GetCurrentAsync(repo.Root));
    }

    [GitFact]
    public async Task Advise_PubspecChanged_RerunRequired()
    {
        using var repo = await GitTestRepo.CreateAsync();
        var oldCommit = await repo.Service.GetHeadCommitAsync(repo.Root);
        repo.Write("pubspec.yaml", "name: app\ndependencies:\n  http: any\n");
        await repo.CommitAllAsync("add dep");
        var newCommit = await repo.Service.GetHeadCommitAsync(repo.Root);

        Assert.NotEqual(oldCommit, newCommit);
        Assert.Equal(BranchChangeAdvice.RerunRequired, await repo.Service.AdviseAfterSwitchAsync(repo.Root, oldCommit, newCommit));
    }

    [GitFact]
    public async Task Advise_OnlyDartChanged_HotRestart()
    {
        using var repo = await GitTestRepo.CreateAsync();
        var oldCommit = await repo.Service.GetHeadCommitAsync(repo.Root);
        repo.Write("lib/main.dart", "void main() { print('x'); }\n");
        await repo.CommitAllAsync("edit dart");
        var newCommit = await repo.Service.GetHeadCommitAsync(repo.Root);

        Assert.Equal(BranchChangeAdvice.HotRestart, await repo.Service.AdviseAfterSwitchAsync(repo.Root, oldCommit, newCommit));
    }

    [GitFact]
    public async Task HeadWatcher_FiresOnExternalSwitch()
    {
        using var repo = await GitTestRepo.CreateAsync();
        await repo.GitAsync("branch", "feature/x");
        var headPath = await repo.Service.GetHeadFilePathAsync(repo.Root);
        Assert.True(File.Exists(headPath));

        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new HeadWatcher(headPath);
        watcher.Changed += () => fired.TrySetResult();

        await repo.GitAsync("switch", "feature/x"); // "external" switch, not through GitService

        var winner = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(fired.Task, winner);
    }

    [GitFact]
    public async Task RepoPathWithSpaces_Works()
    {
        using var repo = await GitTestRepo.CreateAsync("my repo (x)");
        await repo.GitAsync("branch", "feature/x");

        Assert.True(await repo.Service.IsRepoAsync(repo.Root));
        Assert.Equal(["feature/x", "main"], await repo.Service.GetLocalBranchesAsync(repo.Root));
        Assert.Equal("main", await repo.Service.GetCurrentAsync(repo.Root));
        var head = await repo.Service.GetHeadFilePathAsync(repo.Root);
        Assert.True(File.Exists(head), head);
        Assert.True((await repo.Service.SwitchAsync(repo.Root, "feature/x")).Success);
    }
}
