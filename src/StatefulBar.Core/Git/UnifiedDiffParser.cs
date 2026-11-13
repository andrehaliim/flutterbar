namespace StatefulBar.Core.Git;

public enum DiffLineKind
{
    /// <summary><c>@@ -a,b +c,d @@ context</c>.</summary>
    Hunk,
    Context,
    Added,
    Removed,

    /// <summary>Not file content: "Binary file", "No newline at end of file", mode changes, a cut diff.</summary>
    Note,
}

/// <summary>One display row of a diff; line numbers are 1-based, null on the side the row does not exist in.</summary>
public sealed record DiffLine(DiffLineKind Kind, int? OldNumber, int? NewNumber, string Text);

/// <summary>Turns the unified diff of ONE file (<c>git diff</c> output) into display rows.</summary>
public static class UnifiedDiffParser
{
    public const string BinaryNote = "Binary file";
    public const string NoNewlineNote = "No newline at end of file";

    public static IReadOnlyList<DiffLine> Parse(string diff, int maxLines = int.MaxValue)
    {
        var rows = new List<DiffLine>();
        int oldNo = 0, newNo = 0;
        var inHunk = false;
        string? oldMode = null;
        var lines = diff.Split('\n');
        // A trailing '\n' leaves one empty field that is not a line.
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        for (var i = 0; i < count; i++)
        {
            if (rows.Count >= maxLines)
            {
                rows.Add(new DiffLine(DiffLineKind.Note, null, null, $"Diff cut at {maxLines} lines."));
                break;
            }
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = TryParseHunk(line, out oldNo, out newNo);
                rows.Add(new DiffLine(DiffLineKind.Hunk, null, null, line));
                continue;
            }
            if (!inHunk)
            {
                // Header lines before the first hunk; only a few carry something worth showing.
                if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
                {
                    rows.Add(new DiffLine(DiffLineKind.Note, null, null, BinaryNote));
                }
                else if (line.StartsWith("old mode ", StringComparison.Ordinal))
                {
                    oldMode = line["old mode ".Length..];
                }
                else if (line.StartsWith("new mode ", StringComparison.Ordinal))
                {
                    rows.Add(new DiffLine(DiffLineKind.Note, null, null, $"File mode {oldMode ?? "?"} → {line["new mode ".Length..]}"));
                }
                continue;
            }
            if (line.StartsWith("diff ", StringComparison.Ordinal))
            {
                inHunk = false; // a second file (rename pair): its header follows
                continue;
            }
            if (line.Length == 0)
            {
                rows.Add(new DiffLine(DiffLineKind.Context, ++oldNo, ++newNo, "")); // context line whose space was stripped
                continue;
            }
            switch (line[0])
            {
                case '+':
                    rows.Add(new DiffLine(DiffLineKind.Added, null, ++newNo, line[1..]));
                    break;
                case '-':
                    rows.Add(new DiffLine(DiffLineKind.Removed, ++oldNo, null, line[1..]));
                    break;
                case ' ':
                    rows.Add(new DiffLine(DiffLineKind.Context, ++oldNo, ++newNo, line[1..]));
                    break;
                case '\\':
                    rows.Add(new DiffLine(DiffLineKind.Note, null, null, NoNewlineNote));
                    break;
            }
        }
        return rows;
    }

    // "@@ -12,7 +12,9 @@ ..." (a count may be missing: "-3 +3"). The numbers are the line BEFORE the first row.
    private static bool TryParseHunk(string line, out int oldStart, out int newStart)
    {
        oldStart = newStart = 0;
        var end = line.IndexOf("@@", 2, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }
        var ranges = line[2..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (ranges.Length < 2 || !TryStart(ranges[0], '-', out var o) || !TryStart(ranges[^1], '+', out var n))
        {
            return false;
        }
        oldStart = o - 1;
        newStart = n - 1;
        return true;
    }

    private static bool TryStart(string range, char sign, out int start)
    {
        start = 0;
        if (range.Length < 2 || range[0] != sign)
        {
            return false;
        }
        var comma = range.IndexOf(',');
        var number = comma < 0 ? range[1..] : range[1..comma];
        return int.TryParse(number, out start);
    }
}
