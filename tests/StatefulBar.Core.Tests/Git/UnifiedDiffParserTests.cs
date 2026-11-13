using StatefulBar.Core.Git;

namespace StatefulBar.Core.Tests.Git;

public class UnifiedDiffParserTests
{
    [Fact]
    public void Parse_SkipsHeaders_NumbersBothSides()
    {
        const string diff = """
            diff --git a/lib/home.dart b/lib/home.dart
            index 1111111..2222222 100644
            --- a/lib/home.dart
            +++ b/lib/home.dart
            @@ -12,4 +12,5 @@ class Home
               build() {
            -    old();
            +    first();
            +    second();
               }

            """;

        var rows = UnifiedDiffParser.Parse(diff);

        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Hunk, null, null, "@@ -12,4 +12,5 @@ class Home"),
                new DiffLine(DiffLineKind.Context, 12, 12, "  build() {"),
                new DiffLine(DiffLineKind.Removed, 13, null, "    old();"),
                new DiffLine(DiffLineKind.Added, null, 13, "    first();"),
                new DiffLine(DiffLineKind.Added, null, 14, "    second();"),
                new DiffLine(DiffLineKind.Context, 14, 15, "  }"),
            ],
            rows);
    }

    [Fact]
    public void Parse_HunkWithoutCounts_AndCrLf()
    {
        var rows = UnifiedDiffParser.Parse("@@ -3 +3 @@\r\n-a\r\n+b\r\n");

        Assert.Equal(new DiffLine(DiffLineKind.Removed, 3, null, "a"), rows[1]);
        Assert.Equal(new DiffLine(DiffLineKind.Added, null, 3, "b"), rows[2]);
    }

    [Fact]
    public void Parse_NewFile_StartsAtLineOne()
    {
        var rows = UnifiedDiffParser.Parse("new file mode 100644\n--- /dev/null\n+++ b/x\n@@ -0,0 +1,2 @@\n+a\n+b\n");

        Assert.Equal([1, 2], rows.Where(r => r.Kind == DiffLineKind.Added).Select(r => r.NewNumber!.Value));
    }

    [Fact]
    public void Parse_Binary_IsOneNote()
    {
        var rows = UnifiedDiffParser.Parse("diff --git a/logo.png b/logo.png\nindex 1..2 100644\nBinary files a/logo.png and b/logo.png differ\n");

        Assert.Equal([new DiffLine(DiffLineKind.Note, null, null, UnifiedDiffParser.BinaryNote)], rows);
    }

    [Fact]
    public void Parse_NoNewlineMarker_IsNote()
    {
        var rows = UnifiedDiffParser.Parse("@@ -1 +1 @@\n-a\n\\ No newline at end of file\n+a\n");

        Assert.Equal(DiffLineKind.Note, rows[2].Kind);
        Assert.Equal(UnifiedDiffParser.NoNewlineNote, rows[2].Text);
    }

    [Fact]
    public void Parse_ModeChangeOnly_IsNote()
    {
        var rows = UnifiedDiffParser.Parse("diff --git a/run.sh b/run.sh\nold mode 100644\nnew mode 100755\n");

        Assert.Equal([new DiffLine(DiffLineKind.Note, null, null, "File mode 100644 → 100755")], rows);
    }

    [Fact]
    public void Parse_EmptyContextLine_KeepsNumbering()
    {
        var rows = UnifiedDiffParser.Parse("@@ -1,3 +1,3 @@\n a\n\n-b\n+c\n");

        Assert.Equal(new DiffLine(DiffLineKind.Context, 2, 2, ""), rows[2]);
        Assert.Equal(new DiffLine(DiffLineKind.Removed, 3, null, "b"), rows[3]);
    }

    [Fact]
    public void Parse_CutAtMaxLines_AddsNote()
    {
        var diff = "@@ -0,0 +1,10 @@\n" + string.Concat(Enumerable.Range(1, 10).Select(i => $"+{i}\n"));

        var rows = UnifiedDiffParser.Parse(diff, maxLines: 4);

        Assert.Equal(5, rows.Count);
        Assert.Equal(new DiffLine(DiffLineKind.Note, null, null, "Diff cut at 4 lines."), rows[^1]);
    }

    [Fact]
    public void Parse_Empty_NoRows() => Assert.Empty(UnifiedDiffParser.Parse(""));
}
