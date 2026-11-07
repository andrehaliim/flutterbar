using FlutterBar.Core.Git;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Tests.Git;

namespace FlutterBar.Core.Tests.Orchestration;

public class BranchFlowTests
{
    [GitFact]
    public async Task Read_NotARepo_HidesChip()
    {
        using var repo = new GitTestRepo(); // no git init

        var state = await BranchFlow.ReadAsync(repo.Service, repo.Root);

        Assert.False(state.IsRepo);
        Assert.Null(state.Branch);
    }

    [GitFact]
    public async Task Read_Repo_ReturnsCurrentBranch()
    {
        using var repo = await GitTestRepo.CreateAsync();

        var state = await BranchFlow.ReadAsync(repo.Service, repo.Root);

        Assert.True(state.IsRepo);
        Assert.Equal("main", state.Branch);
        Assert.False(state.Unborn);
        Assert.Null(state.Error);
    }

    [GitFact]
    public async Task Read_UnbornBranch_UsesSymbolicRef()
    {
        using var repo = new GitTestRepo();
        await repo.GitAsync("init", "-b", "fitur-baru");

        var state = await BranchFlow.ReadAsync(repo.Service, repo.Root);

        Assert.True(state.IsRepo);
        Assert.Equal("fitur-baru", state.Branch);
        Assert.True(state.Unborn);
        Assert.Null(state.Error);
        Assert.Contains(UiText.NoCommitYet, state.ToolTip);
    }

    [GitFact]
    public async Task SymbolicBranch_DetachedHead_IsNull()
    {
        using var repo = await GitTestRepo.CreateAsync();
        var sha = await repo.GitAsync("rev-parse", "HEAD");
        await repo.GitAsync("switch", "--detach", sha);

        Assert.Null(await repo.Service.GetSymbolicBranchAsync(repo.Root));
    }

    [GitFact]
    public async Task Switch_Failure_ReturnsGitStderr()
    {
        using var repo = await GitTestRepo.CreateAsync();

        var outcome = await BranchFlow.SwitchAsync(repo.Service, repo.Root, "no-such-branch", sessionActive: true);

        Assert.False(outcome.Success);
        Assert.Contains("no-such-branch", outcome.Message);
        Assert.Null(outcome.Advice);
    }

    [GitFact]
    public async Task Switch_NoSession_NoAdvice()
    {
        using var repo = await GitTestRepo.CreateAsync();
        await repo.GitAsync("branch", "dev");

        var outcome = await BranchFlow.SwitchAsync(repo.Service, repo.Root, "dev", sessionActive: false);

        Assert.True(outcome.Success);
        Assert.Null(outcome.Advice);
        Assert.Equal("dev", await repo.Service.GetCurrentAsync(repo.Root));
    }

    [GitFact]
    public async Task Switch_SessionActive_SamePubspec_AdvisesHotRestart()
    {
        using var repo = await GitTestRepo.CreateAsync();
        await repo.GitAsync("switch", "-c", "dev");
        repo.Write("lib/main.dart", "void main() { print(1); }\n");
        await repo.CommitAllAsync("code only");
        await repo.GitAsync("switch", "main");

        var outcome = await BranchFlow.SwitchAsync(repo.Service, repo.Root, "dev", sessionActive: true);

        Assert.True(outcome.Success);
        Assert.Equal(BranchChangeAdvice.HotRestart, outcome.Advice);
    }

    [GitFact]
    public async Task Switch_SessionActive_PubspecChanged_AdvisesRerun()
    {
        using var repo = await GitTestRepo.CreateAsync();
        await repo.GitAsync("switch", "-c", "dev");
        repo.Write("pubspec.yaml", "name: app\nversion: 2.0.0\n");
        await repo.CommitAllAsync("bump");
        await repo.GitAsync("switch", "main");

        var outcome = await BranchFlow.SwitchAsync(repo.Service, repo.Root, "dev", sessionActive: true);

        Assert.True(outcome.Success);
        Assert.Equal(BranchChangeAdvice.RerunRequired, outcome.Advice);
    }

    [Fact]
    public void AdviceToast_UsesSpecCopy()
    {
        Assert.Equal(("Branch changed. Hot restart?", "Hot restart"), BranchFlow.AdviceToast(BranchChangeAdvice.HotRestart));
        Assert.Equal(("pubspec changed. Stop and run again.", (string?)null), BranchFlow.AdviceToast(BranchChangeAdvice.RerunRequired));
    }
}
