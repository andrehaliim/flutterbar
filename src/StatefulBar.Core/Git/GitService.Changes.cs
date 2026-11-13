namespace StatefulBar.Core.Git;

/// <summary>Read-only view of the uncommitted changes against HEAD: the changed-file list and one file's diff.</summary>
public sealed partial class GitService
{
    /// <summary>Untracked files larger than this are neither counted nor shown.</summary>
    public const long MaxUntrackedBytes = 1024 * 1024;

    /// <summary>A file with more changed lines than this is not diffed (the flyout would only stall).</summary>
    public const int MaxDiffChangedLines = 5000;

    /// <summary>Rows kept from one diff; the rest is cut with a note.</summary>
    public const int MaxDiffRows = 3000;

    public const string NoChangesNote = "No changes against HEAD.";

    /// <summary>
    /// Every entry of <c>git status</c> for the whole repository (the same set <c>git add -A</c> commits), with line counts
    /// from <c>git diff HEAD --numstat</c>; untracked files are counted by reading them. Throws <see cref="InvalidOperationException"/>
    /// when git fails.
    /// </summary>
    public async Task<GitChanges> GetChangesAsync(string root, CancellationToken ct)
    {
        var top = await GetTopLevelAsync(root, ct).ConfigureAwait(false);
        var status = await RunAsync(top, CommandTimeout, null, ct, "--no-optional-locks", "status", "--porcelain=v2", "-z", "--untracked-files=all")
            .ConfigureAwait(false);
        if (status.ExitCode != 0)
        {
            throw new InvalidOperationException($"git status failed (exit {status.ExitCode}): {FailureText(status)}");
        }
        var entries = GitChangeParser.ParseStatus(status.Stdout);
        IReadOnlyDictionary<string, GitChangeParser.NumstatEntry> counts = new Dictionary<string, GitChangeParser.NumstatEntry>();
        if (entries.Any(e => e.Kind != GitChangeKind.Untracked))
        {
            var baseRef = await DiffBaseAsync(top, ct).ConfigureAwait(false);
            var numstat = await RunAsync(top, CommandTimeout, null, ct, "--no-optional-locks", "diff", baseRef, "--numstat", "-z", "--find-renames", "--no-ext-diff")
                .ConfigureAwait(false);
            if (numstat.ExitCode != 0)
            {
                throw new InvalidOperationException($"git diff --numstat failed (exit {numstat.ExitCode}): {FailureText(numstat)}");
            }
            counts = GitChangeParser.ParseNumstat(numstat.Stdout);
        }

        var files = new List<GitChangedFile>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Kind == GitChangeKind.Untracked)
            {
                var (lines, binary) = CountUntracked(FullPath(top, entry.Path));
                files.Add(new GitChangedFile(entry.Path, null, entry.Kind, lines, lines is null ? null : 0, binary));
            }
            else if (counts.TryGetValue(entry.Path, out var count))
            {
                files.Add(new GitChangedFile(entry.Path, entry.OldPath, entry.Kind, count.Added, count.Deleted, count.Added is null && count.Deleted is null));
            }
            else
            {
                files.Add(new GitChangedFile(entry.Path, entry.OldPath, entry.Kind, null, null, false));
            }
        }
        files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return new GitChanges(top, files);
    }

    /// <summary>
    /// Diff rows of one file from <see cref="GetChangesAsync"/>: <c>git diff HEAD</c> for tracked files (staged and unstaged
    /// together), every line as added for an untracked one. Binary, oversized and unchanged files give a single note row.
    /// </summary>
    public async Task<IReadOnlyList<DiffLine>> GetFileDiffAsync(string topLevel, GitChangedFile file, CancellationToken ct)
    {
        if (file.IsBinary)
        {
            return [Note(UnifiedDiffParser.BinaryNote)];
        }
        if ((file.Added ?? 0) + (file.Deleted ?? 0) > MaxDiffChangedLines)
        {
            return [Note($"Too large to show here ({file.Added + file.Deleted} changed lines). Open the file in your editor.")];
        }
        if (file.Kind == GitChangeKind.Untracked)
        {
            return await Task.Run(() => UntrackedDiff(FullPath(topLevel, file.Path)), ct).ConfigureAwait(false);
        }

        var baseRef = await DiffBaseAsync(topLevel, ct).ConfigureAwait(false);
        List<string> args = ["--no-optional-locks", "diff", baseRef, "--no-color", "--no-ext-diff", "--find-renames", "--", Literal(file.Path)];
        if (file is { Kind: GitChangeKind.Renamed, OldPath: { } old })
        {
            args.Add(Literal(old));
        }
        var result = await RunAsync(topLevel, CommandTimeout, null, ct, [.. args]).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git diff failed (exit {result.ExitCode}): {FailureText(result)}");
        }
        var rows = UnifiedDiffParser.Parse(result.Stdout, MaxDiffRows);
        return rows.Count == 0 ? [Note(NoChangesNote)] : rows;
    }

    private async Task<string> GetTopLevelAsync(string root, CancellationToken ct)
    {
        var result = await RunAsync(root, CommandTimeout, null, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        var path = result.Stdout.Trim();
        if (result.ExitCode != 0 || path.Length == 0)
        {
            throw new InvalidOperationException($"git rev-parse --show-toplevel failed (exit {result.ExitCode}): {FailureText(result)}");
        }
        return Path.GetFullPath(path); // git prints C:/x/y
    }

    /// <summary>"HEAD", or the empty tree in a repository without commits (hashed by git, so SHA-256 repositories work too).</summary>
    private async Task<string> DiffBaseAsync(string top, CancellationToken ct)
    {
        var head = await RunAsync(top, CommandTimeout, null, ct, "rev-parse", "--verify", "--quiet", "HEAD^{commit}").ConfigureAwait(false);
        if (head.ExitCode == 0)
        {
            return "HEAD";
        }
        var empty = Path.GetTempFileName();
        try
        {
            var tree = await RunAsync(top, CommandTimeout, null, ct, "hash-object", "-t", "tree", empty).ConfigureAwait(false);
            if (tree.ExitCode != 0)
            {
                throw new InvalidOperationException($"git hash-object failed (exit {tree.ExitCode}): {FailureText(tree)}");
            }
            return tree.Stdout.Trim();
        }
        finally
        {
            File.Delete(empty);
        }
    }

    // Pathspec magic: relative to the top level and no glob characters ('*', '[' in a file name stay literal).
    private static string Literal(string path) => ":(top,literal)" + path;

    private static string FullPath(string top, string gitPath) => Path.Combine(top, gitPath.Replace('/', Path.DirectorySeparatorChar));

    private static DiffLine Note(string text) => new(DiffLineKind.Note, null, null, text);

    /// <summary>Line count and binary flag of an untracked file; null lines when unreadable or too large.</summary>
    private static (int? Lines, bool Binary) CountUntracked(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxUntrackedBytes)
            {
                return (null, false);
            }
            var bytes = File.ReadAllBytes(path);
            if (IsBinary(bytes))
            {
                return (null, true);
            }
            var lines = bytes.Count(b => b == (byte)'\n');
            if (bytes.Length > 0 && bytes[^1] != (byte)'\n')
            {
                lines++;
            }
            return (lines, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, false);
        }
    }

    private static IReadOnlyList<DiffLine> UntrackedDiff(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return [Note(NoChangesNote)];
            }
            if (info.Length > MaxUntrackedBytes)
            {
                return [Note("Too large to show here. Open the file in your editor.")];
            }
            var bytes = File.ReadAllBytes(path);
            if (IsBinary(bytes))
            {
                return [Note(UnifiedDiffParser.BinaryNote)];
            }
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            var lines = text.Split('\n');
            var count = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
            if (count == 0)
            {
                return [Note("Empty file")];
            }
            var rows = new List<DiffLine>(Math.Min(count, MaxDiffRows) + 2) { new(DiffLineKind.Hunk, null, null, $"@@ -0,0 +1,{count} @@") };
            for (var i = 0; i < count; i++)
            {
                if (rows.Count > MaxDiffRows)
                {
                    rows.Add(Note($"Diff cut at {MaxDiffRows} lines."));
                    break;
                }
                rows.Add(new DiffLine(DiffLineKind.Added, null, i + 1, lines[i].TrimEnd('\r')));
            }
            return rows;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [Note($"Can't read the file: {ex.Message}")];
        }
    }

    // Same heuristic as git: a NUL byte in the first 8000 bytes.
    private static bool IsBinary(byte[] bytes) => Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0;
}
