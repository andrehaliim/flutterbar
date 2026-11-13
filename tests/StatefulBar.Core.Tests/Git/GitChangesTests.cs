using StatefulBar.Core.Git;

namespace StatefulBar.Core.Tests.Git;

/// <summary>The changed-files list and per-file diff of the branch flyout, and commit without push.</summary>
public class GitChangesTests
{
    [Fact]
    public void ParseStatus_AllEntryTypes()
    {
        const string output =
            "# branch.oid abc\0# branch.head main\0" +
            "1 .M N... 100644 100644 100644 a a lib/main.dart\0" +
            "1 A. N... 000000 100644 100644 0 b lib/new file.dart\0" +
            "1 .D N... 100644 100644 000000 a a old.txt\0" +
            "2 R. N... 100644 100644 100644 a a R100 lib/b.dart\0lib/a.dart\0" +
            "u UU N... 100644 100644 100644 100644 a b c pubspec.yaml\0" +
            "? notes/todo.md\0";

        var entries = GitChangeParser.ParseStatus(output);

        Assert.Equal(
            [
                new GitChangeParser.StatusEntry("lib/main.dart", null, GitChangeKind.Modified),
                new GitChangeParser.StatusEntry("lib/new file.dart", null, GitChangeKind.Added),
                new GitChangeParser.StatusEntry("old.txt", null, GitChangeKind.Deleted),
                new GitChangeParser.StatusEntry("lib/b.dart", "lib/a.dart", GitChangeKind.Renamed),
                new GitChangeParser.StatusEntry("pubspec.yaml", null, GitChangeKind.Conflicted),
                new GitChangeParser.StatusEntry("notes/todo.md", null, GitChangeKind.Untracked),
            ],
            entries);
    }

    [Fact]
    public void ParseNumstat_CountsBinaryAndRename()
    {
        var counts = GitChangeParser.ParseNumstat("3\t1\tlib/main.dart\0-\t-\tlogo.png\0" + "0\t0\t\0lib/a.dart\0lib/b.dart\0");

        Assert.Equal(new GitChangeParser.NumstatEntry(3, 1), counts["lib/main.dart"]);
        Assert.Equal(new GitChangeParser.NumstatEntry(null, null), counts["logo.png"]);
        Assert.Equal(new GitChangeParser.NumstatEntry(0, 0), counts["lib/b.dart"]);
        Assert.Equal(3, counts.Count);
    }

    [Fact]
    public void ChangedFile_NameAndFolder()
    {
        var file = new GitChangedFile("lib/screens/home.dart", null, GitChangeKind.Modified, 1, 0, false);
        Assert.Equal("home.dart", file.Name);
        Assert.Equal("lib/screens", file.Folder);
        Assert.Equal("", new GitChangedFile("pubspec.yaml", null, GitChangeKind.Modified, 1, 0, false).Folder);
    }

    [GitFact]
    public async Task Changes_ModifiedUntrackedDeleted_WithCounts()
    {
        using var repo = await GitTestRepo.CreateAsync();
        repo.Write("lib/main.dart", "void main() {\n  run();\n}\n");
        repo.Write("notes/todo.md", "a\nb\nc");
        File.Delete(Path.Combine(repo.Root, "pubspec.yaml"));

        var changes = await repo.Service.GetChangesAsync(repo.Root, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(repo.Root), changes.TopLevel);
        Assert.Equal(
            [
                new GitChangedFile("lib/main.dart", null, GitChangeKind.Modified, 3, 1, false),
                new GitChangedFile("notes/todo.md", null, GitChangeKind.Untracked, 3, 0, false),
                new GitChangedFile("pubspec.yaml", null, GitChangeKind.Deleted, 0, 1, false),
            ],
            changes.Files);
        Assert.Equal(changes.Files.Count, (await repo.Service.GetStatusAsync(repo.Root, CancellationToken.None)).ChangedFiles);
    }

    [GitFact]
    public async Task Diff_Modified_StagedAndUnstagedTogether()
    {
        using var repo = await GitTestRepo.CreateAsync();
        repo.Write("lib/main.dart", "void main() {}\n// staged\n");
        await repo.GitAsync("add", "lib/main.dart");
        repo.Write("lib/main.dart", "void main() {}\n// staged\n// unstaged\n");
        var file = (await repo.Service.GetChangesAsync(repo.Root, CancellationToken.None)).Files.Single();

        var rows = await repo.Service.GetFileDiffAsync(repo.Root, file, CancellationToken.None);

        Assert.Equal(new GitChangedFile("lib/main.dart", null, GitChangeKind.Modified, 2, 0, false), file);
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Context, 1, 1, "void main() {}"),
                new DiffLine(DiffLineKind.Added, null, 2, "// staged"),
                new DiffLine(DiffLineKind.Added, null, 3, "// unstaged"),
            ],
            rows.Skip(1));
    }

    [GitFact]
    public async Task Diff_Untracked_EveryLineAdded()
    {
        using var repo = await GitTestRepo.CreateAsync();
        repo.Write("lib/new.dart", "a\r\nb\n");
        var file = (await repo.Service.GetChangesAsync(repo.Root, CancellationToken.None)).Files.Single();

        var rows = await repo.Service.GetFileDiffAsync(repo.Root, file, CancellationToken.None);

        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Hunk, null, null, "@@ -0,0 +1,2 @@"),
                new DiffLine(DiffLineKind.Added, null, 1, "a"),
                new DiffLine(DiffLineKind.Added, null, 2, "b"),
            ],
            rows);
    }

    [GitFact]
    public async Task Diff_Deleted_EveryLineRemoved()
    {
        using var repo = await GitTestRepo.CreateAsync();
        File.Delete(Path.Combine(repo.Root, "lib", "main.dart"));
        var file = (await repo.Service.GetChangesAsync(repo.Root, CancellationToken.None)).Files.Single();

        var rows = await repo.Service.GetFileDiffAsync(repo.Root, file, CancellationToken.None);

        Assert.Equal(new DiffLine(DiffLineKind.Removed, 1, null, "void main() {}"), rows[^1]);
    }

    [GitFact]
    public async Task Changes_StagedRename_PairsPaths_AndDiffs()
    {
        using var repo = await GitTestRepo.CreateAsync();
        await repo.GitAsync("mv", "lib/main.dart", "lib/app.dart");
        repo.Write("lib/app.dart", "void main() {}\n// more\n");

        var changes = await repo.Service.GetChangesAsync(repo.Root, CancellationToken.None);
        var file = changes.Files.Single();
        var rows = await repo.Service.GetFileDiffAsync(changes.TopLevel, file, CancellationToken.None);

        Assert.Equal(new GitChangedFile("lib/app.dart", "lib/main.dart", GitChangeKind.Renamed, 1, 0, false), file);
        Assert.Equal(new DiffLine(DiffLineKind.Added, null, 2, "// more"), rows[^1]);
    }

    [GitFact]
    public async Task Changes_Binary_TrackedAndUntracked()
    {
        using var repo = await GitTestRepo.CreateAsync();
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "logo.png"), [0x89, 0x50, 0x00, 0x01]);
        await repo.CommitAllAsync("logo");
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "logo.png"), [0x89, 0x50, 0x00, 0x02]);
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "icon.png"), [0x00, 0x01]);

        var changes = await repo.Service.GetChangesAsync(repo.Root, CancellationToken.None);

        Assert.All(changes.Files, f => Assert.True(f.IsBinary, f.Path));
        foreach (var file in changes.Files)
        {
            var rows = await repo.Service.GetFileDiffAsync(changes.TopLevel, file, CancellationToken.None);
            Assert.Equal([new DiffLine(DiffLineKind.Note, null, null, UnifiedDiffParser.BinaryNote)], rows);
        }
    }

    [GitFact]
    public async Task Changes_ProjectInSubfolder_PathsFromTopLevel_GlobCharactersLiteral()
    {
        using var repo = await GitTestRepo.CreateAsync();
        repo.Write("apps/my-app/lib/[id] page.dart", "x\n");
        await repo.CommitAllAsync("page");
        repo.Write("apps/my-app/lib/[id] page.dart", "y\n");
        repo.Write("apps/my-app/lib/i page.dart", "decoy\n"); // would match the glob [id]
        await repo.CommitAllAsync("decoy");
        repo.Write("apps/my-app/lib/[id] page.dart", "z\n");
        var project = Path.Combine(repo.Root, "apps", "my-app");

        var changes = await repo.Service.GetChangesAsync(project, CancellationToken.None);
        var file = changes.Files.Single();
        var rows = await repo.Service.GetFileDiffAsync(changes.TopLevel, file, CancellationToken.None);

        Assert.Equal("apps/my-app/lib/[id] page.dart", file.Path);
        Assert.Equal(["-y", "+z"], rows.Where(r => r.Kind != DiffLineKind.Hunk).Select(r => (r.Kind == DiffLineKind.Added ? "+" : "-") + r.Text));
    }

    [GitFact]
    public async Task Changes_RepoWithoutCommits_DiffsAgainstEmptyTree()
    {
        using var repo = new GitTestRepo();
        await repo.GitAsync("init", "-b", "main");
        repo.Write("staged.txt", "s\n");
        await repo.GitAsync("add", "staged.txt");
        repo.Write("loose.txt", "l\n");

        var changes = await repo.Service.GetChangesAsync(repo.Root, CancellationToken.None);
        var staged = changes.Files.Single(f => f.Path == "staged.txt");
        var rows = await repo.Service.GetFileDiffAsync(changes.TopLevel, staged, CancellationToken.None);

        Assert.Equal(GitChangeKind.Added, staged.Kind);
        Assert.Equal(GitChangeKind.Untracked, changes.Files.Single(f => f.Path == "loose.txt").Kind);
        Assert.Equal(new DiffLine(DiffLineKind.Added, null, 1, "s"), rows[^1]);
    }

    [GitFact]
    public async Task Diff_TooManyChangedLines_IsNote()
    {
        using var repo = await GitTestRepo.CreateAsync();
        var file = new GitChangedFile("big.txt", null, GitChangeKind.Modified, GitService.MaxDiffChangedLines, 1, false);

        var rows = await repo.Service.GetFileDiffAsync(repo.Root, file, CancellationToken.None);

        Assert.Equal(DiffLineKind.Note, rows.Single().Kind);
        Assert.StartsWith("Too large to show here", rows.Single().Text);
    }

    [GitFact]
    public async Task Commit_CommitsAllWithoutPushing()
    {
        using var repo = await GitTestRepo.CreateAsync("work");
        var remote = await repo.AddBareRemoteAsync();
        var remoteHead = await repo.GitInAsync(repo.Temp, "--git-dir", remote, "rev-parse", "main");
        repo.Write("pubspec.yaml", "name: app2\n");
        repo.Write("lib/new.dart", "// new\n");

        var result = await repo.Service.CommitAsync(repo.Root, "feat: wip", CancellationToken.None);
        var status = await repo.Service.GetStatusAsync(repo.Root, CancellationToken.None);

        Assert.Equal(new GitResult(true, "Committed to main"), result);
        Assert.Equal("feat: wip", await repo.GitAsync("log", "-1", "--format=%s"));
        Assert.Equal((0, 1), (status.ChangedFiles, status.Ahead));
        Assert.Equal(remoteHead, await repo.GitInAsync(repo.Temp, "--git-dir", remote, "rev-parse", "main"));
    }

    [GitFact]
    public async Task Commit_RefusesEmptyMessage_NothingToCommit_DetachedHead()
    {
        using var repo = await GitTestRepo.CreateAsync();

        Assert.Equal(new GitResult(false, GitService.EnterMessage), await repo.Service.CommitAsync(repo.Root, "  ", CancellationToken.None));
        Assert.Equal(new GitResult(false, "Nothing to commit."), await repo.Service.CommitAsync(repo.Root, "msg", CancellationToken.None));

        await repo.GitAsync("checkout", "--detach");
        repo.Write("lib/new.dart", "// new\n");
        Assert.Equal(new GitResult(false, GitService.DetachedHead), await repo.Service.CommitAsync(repo.Root, "msg", CancellationToken.None));
    }

    [GitFact]
    public async Task Commit_UnfinishedMerge_Refused()
    {
        using var repo = await GitTestRepo.CreateAsync();
        await repo.GitAsync("switch", "-c", "other");
        repo.Write("lib/main.dart", "void main() { other(); }\n");
        await repo.CommitAllAsync("other edit");
        await repo.GitAsync("switch", "main");
        repo.Write("lib/main.dart", "void main() { mine(); }\n");
        await repo.CommitAllAsync("main edit");
        var merge = await new StatefulBar.Core.Processes.ProcessRunner().RunAsync(
            new StatefulBar.Core.Processes.ProcessSpec(GitTestRepo.GitExe!, ["merge", "other"], repo.Root), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotEqual(0, merge.ExitCode); // conflict leaves the merge in progress
        var head = await repo.GitAsync("rev-parse", "HEAD");

        var result = await repo.Service.CommitAsync(repo.Root, "resolve", CancellationToken.None);

        Assert.Equal(new GitResult(false, GitService.ResolveConflictsFirst), result);
        Assert.Equal(head, await repo.GitAsync("rev-parse", "HEAD"));
    }
}
