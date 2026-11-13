namespace StatefulBar.Core.Git;

public enum GitChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Conflicted,
}

/// <summary>
/// One changed file against HEAD (what <c>git add -A</c> + commit would record). <paramref name="Path"/> is relative to the
/// repository top level with '/' separators, as git prints it; <paramref name="OldPath"/> is set for renames and copies.
/// <paramref name="Added"/> / <paramref name="Deleted"/> are null when unknown (binary, or an untracked file too large to count).
/// </summary>
public sealed record GitChangedFile(string Path, string? OldPath, GitChangeKind Kind, int? Added, int? Deleted, bool IsBinary)
{
    /// <summary>File name without its folder (the list shows the folder separately).</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>Folder part of <see cref="Path"/> ("" at the top level).</summary>
    public string Folder => Path.LastIndexOf('/') is var i and > 0 ? Path[..i] : "";
}

/// <summary>Changed files of the repository whose top level is <paramref name="TopLevel"/> (a full Windows path).</summary>
public sealed record GitChanges(string TopLevel, IReadOnlyList<GitChangedFile> Files);

/// <summary>Parsers for the NUL-separated (<c>-z</c>) output of <c>git status --porcelain=v2</c> and <c>git diff --numstat</c>.</summary>
public static class GitChangeParser
{
    public sealed record StatusEntry(string Path, string? OldPath, GitChangeKind Kind);

    public sealed record NumstatEntry(int? Added, int? Deleted);

    /// <summary>Entries of <c>git status --porcelain=v2 -z</c> in git's order; headers (<c># ...</c>) and ignored entries are skipped.</summary>
    public static IReadOnlyList<StatusEntry> ParseStatus(string output)
    {
        var fields = output.Split('\0');
        var entries = new List<StatusEntry>();
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field.Length < 2)
            {
                continue;
            }
            switch (field[0])
            {
                case '1':
                    {
                        // 1 XY sub mH mI mW hH hI path
                        var parts = field.Split(' ', 9);
                        if (parts.Length == 9)
                        {
                            entries.Add(new StatusEntry(parts[8], null, OrdinaryKind(parts[1])));
                        }
                        break;
                    }
                case '2':
                    {
                        // 2 XY sub mH mI mW hH hI Xscore path, then the original path as the next field
                        var parts = field.Split(' ', 10);
                        var old = i + 1 < fields.Length ? fields[++i] : null;
                        if (parts.Length == 10)
                        {
                            var kind = parts[8].StartsWith('C') ? GitChangeKind.Copied : GitChangeKind.Renamed;
                            entries.Add(new StatusEntry(parts[9], old, kind));
                        }
                        break;
                    }
                case 'u':
                    {
                        // u XY sub m1 m2 m3 mW h1 h2 h3 path
                        var parts = field.Split(' ', 11);
                        if (parts.Length == 11)
                        {
                            entries.Add(new StatusEntry(parts[10], null, GitChangeKind.Conflicted));
                        }
                        break;
                    }
                case '?' when field[1] == ' ':
                    entries.Add(new StatusEntry(field[2..], null, GitChangeKind.Untracked));
                    break;
            }
        }
        return entries;
    }

    /// <summary>
    /// <c>git diff --numstat -z</c> by (new) path. Binary files have "-" counts (null here). A rename is
    /// <c>added\tdeleted\t\0old\0new\0</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, NumstatEntry> ParseNumstat(string output)
    {
        var fields = output.Split('\0');
        var result = new Dictionary<string, NumstatEntry>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Length; i++)
        {
            var parts = fields[i].Split('\t', 3);
            if (parts.Length != 3)
            {
                continue;
            }
            var path = parts[2];
            if (path.Length == 0)
            {
                // rename / copy: the old and new paths follow as their own fields
                if (i + 2 >= fields.Length)
                {
                    break;
                }
                i += 2;
                path = fields[i];
            }
            result[path] = new NumstatEntry(ParseCount(parts[0]), ParseCount(parts[1]));
        }
        return result;
    }

    private static int? ParseCount(string text) => int.TryParse(text, out var n) ? n : null;

    // XY of an ordinary entry: X = index vs HEAD, Y = worktree vs index ('.' = unchanged).
    private static GitChangeKind OrdinaryKind(string xy)
    {
        if (xy.Contains('D'))
        {
            return GitChangeKind.Deleted;
        }
        if (xy.Contains('A'))
        {
            return GitChangeKind.Added; // staged new file, or intent-to-add (".A")
        }
        if (xy.Contains('T'))
        {
            return GitChangeKind.TypeChanged;
        }
        return GitChangeKind.Modified;
    }
}
