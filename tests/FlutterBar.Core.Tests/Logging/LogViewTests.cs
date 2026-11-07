using FlutterBar.Core.Daemon;
using FlutterBar.Core.Logging;

namespace FlutterBar.Core.Tests.Logging;

public class LogViewTests
{
    private const string DeviceHeader = "════════ Exception caught by widgets library ═══════════════════";
    private static readonly string Footer = new('═', 60);

    private static long _seq;

    private static LogLine L(LogKind kind, string text, long? seq = null) =>
        new(seq ?? Interlocked.Increment(ref _seq), new DateTimeOffset(2026, 11, 7, 10, 20, 30, 456, TimeSpan.Zero), kind, text);

    private static List<LogLine> Lines(long start, params (LogKind Kind, string Text)[] items) =>
        items.Select((x, i) => L(x.Kind, x.Text, start + i)).ToList();

    private static List<LogLine> FixtureLines()
    {
        var texts = File.ReadAllLines(TestPaths.Fixture("daemon", "3.22.3", "runtime-exception.jsonl"))
            .SelectMany(DaemonProtocol.ParseLine)
            .Select(m => m switch { NonJson n => n.Text, AppLog l => l.Log, _ => null })
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();
        // Same classification RunSession applies: header through footer are errors, the rest tool output.
        var result = new List<LogLine>();
        var inException = false;
        long seq = 1;
        foreach (var text in texts)
        {
            var kind = LogKind.Tool;
            if (LogClassifier.IsExceptionHeader(text))
            {
                inException = true;
                kind = LogKind.Error;
            }
            else if (inException)
            {
                kind = LogKind.Error;
                if (LogClassifier.IsExceptionFooter(text))
                {
                    inException = false;
                }
            }
            result.Add(L(kind, text, seq++));
        }
        return result;
    }

    [Fact]
    public void Grouping_RealFixture_HeaderToFooterIsOneCollapsedGroup()
    {
        var lines = FixtureLines();
        var header = lines.FindIndex(l => LogClassifier.IsExceptionHeader(l.Text));
        var footer = lines.FindIndex(l => LogClassifier.IsExceptionFooter(l.Text));
        Assert.True(header > 0 && footer > header + 5);

        var view = new LogView();
        view.Append(lines);

        // The real dump is longer than MaxGroupLines, so it is one block split into continuation groups.
        var groups = view.Entries.Where(e => e.IsGroup).ToList();
        Assert.Equal(footer - header + 1, groups.Sum(g => g.LineCount));
        Assert.Equal((footer - header + 1 + LogView.MaxGroupLines - 1) / LogView.MaxGroupLines, groups.Count);
        Assert.Equal(lines[header].Seq, groups[0].Lines[0].Seq);
        Assert.Equal(lines[footer].Seq, groups[^1].Lines[^1].Seq);
        Assert.False(groups[0].IsContinuation);
        Assert.All(groups.Skip(1), g => Assert.True(g.IsContinuation));
        Assert.All(groups, g => Assert.True(g.Closed && !g.Expanded));

        // collapsed: one row per group, everything else is a plain line
        Assert.Equal(lines.Count - (footer - header + 1) + groups.Count, view.Rows.Count);
        Assert.Equal(groups.Count, view.Rows.Count(r => r.Role == LogRowRole.GroupHeader));
        Assert.DoesNotContain(view.Rows, r => r.Role == LogRowRole.GroupChild);
    }

    [Fact]
    public void Grouping_DeviceForm()
    {
        var view = new LogView();
        view.Append(Lines(1,
            (LogKind.App, "hello"),
            (LogKind.Error, DeviceHeader),
            (LogKind.Error, "The following assertion was thrown"),
            (LogKind.Error, Footer),
            (LogKind.App, "after")));

        var group = Assert.Single(view.Entries, e => e.IsGroup);
        Assert.Equal(3, group.LineCount);
        Assert.Equal(3, view.Rows.Count);
        Assert.Equal(["hello", DeviceHeader, "after"], view.Rows.Select(r => r.Line.Text));
    }

    [Fact]
    public void Grouping_UnterminatedBlockEndsAtFirstNonErrorLine()
    {
        var view = new LogView();
        view.Append(Lines(1,
            (LogKind.Error, DeviceHeader),
            (LogKind.Error, "boom"),
            (LogKind.App, "still running")));

        var group = Assert.Single(view.Entries, e => e.IsGroup);
        Assert.Equal(2, group.LineCount);
        Assert.True(group.Closed);
        Assert.Equal("still running", view.Rows[^1].Line.Text);
        Assert.Equal(LogRowRole.Line, view.Rows[^1].Role);
    }

    [Fact]
    public void Grouping_SeparatorClosesOpenBlock()
    {
        var view = new LogView();
        view.Append(Lines(1, (LogKind.Error, DeviceHeader), (LogKind.Error, "boom"), (LogKind.Separator, "── Hot reload 10:00:00 ──")));
        Assert.Equal(2, view.Rows.Count);
        Assert.Equal(LogKind.Separator, view.Rows[1].Line.Kind);
    }

    [Fact]
    public void Grouping_BlockSpanningTwoAppendCalls()
    {
        var view = new LogView();
        view.Append(Lines(1, (LogKind.Error, DeviceHeader), (LogKind.Error, "a")));
        Assert.False(view.Entries[0].Closed);
        view.Append(Lines(3, (LogKind.Error, "b"), (LogKind.Error, Footer), (LogKind.App, "x")));

        var group = Assert.Single(view.Entries, e => e.IsGroup);
        Assert.Equal(4, group.LineCount);
        Assert.True(group.Closed);
        Assert.Equal(2, view.Rows.Count);
    }

    [Fact]
    public void Expand_InsertsChildRows_CollapseRemovesThem()
    {
        var view = new LogView();
        view.Append(Lines(1,
            (LogKind.App, "before"),
            (LogKind.Error, DeviceHeader),
            (LogKind.Error, "a"),
            (LogKind.Error, Footer),
            (LogKind.App, "after")));
        var group = view.Entries[1];

        view.SetExpanded(group, true);

        Assert.True(group.Expanded);
        Assert.Equal(["before", DeviceHeader, "a", Footer, "after"], view.Rows.Select(r => r.Line.Text));
        Assert.Equal([LogRowRole.Line, LogRowRole.GroupHeader, LogRowRole.GroupChild, LogRowRole.GroupChild, LogRowRole.Line], view.Rows.Select(r => r.Role));

        view.SetExpanded(group, false);
        Assert.Equal(["before", DeviceHeader, "after"], view.Rows.Select(r => r.Line.Text));
    }

    [Fact]
    public void ExpandedOpenGroup_GrowsRowByRow()
    {
        var view = new LogView();
        view.Append(Lines(1, (LogKind.Error, DeviceHeader), (LogKind.Error, "a")));
        view.SetExpanded(view.Entries[0], true);
        Assert.Equal(2, view.Rows.Count);

        view.Append(Lines(3, (LogKind.Error, "b")));

        Assert.Equal([DeviceHeader, "a", "b"], view.Rows.Select(r => r.Line.Text));
        Assert.Equal(3, view.Entries[0].LineCount);
    }

    [Fact]
    public void HeaderRow_RaisesPropertyChangedWhenGroupGrowsOrExpands()
    {
        var view = new LogView();
        view.Append(Lines(1, (LogKind.Error, DeviceHeader)));
        var changed = new List<string?>();
        view.Entries[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        view.Append(Lines(2, (LogKind.Error, "a")));
        view.SetExpanded(view.Entries[0], true);

        Assert.Contains(nameof(LogEntry.LineCount), changed);
        Assert.Contains(nameof(LogEntry.Expanded), changed);
    }

    [Fact]
    public void Search_IsCaseInsensitiveSubstring_AndFlattensGroups()
    {
        var view = new LogView();
        view.Append(Lines(1,
            (LogKind.App, "Hello World"),
            (LogKind.Error, DeviceHeader),
            (LogKind.Error, "the WORLD is flat"),
            (LogKind.Error, Footer),
            (LogKind.App, "nothing")));

        view.SetSearch("world");

        Assert.Equal(["Hello World", "the WORLD is flat"], view.Rows.Select(r => r.Line.Text));
        Assert.All(view.Rows, r => Assert.Equal(LogRowRole.Line, r.Role));

        view.SetSearch("");
        Assert.Equal(3, view.Rows.Count);
        Assert.Contains(view.Rows, r => r.Role == LogRowRole.GroupHeader);
    }

    [Fact]
    public void Search_AppliesToLinesAppendedLater()
    {
        var view = new LogView();
        view.SetSearch("x");
        view.Append(Lines(1, (LogKind.App, "ax"), (LogKind.App, "b")));
        view.Append(Lines(3, (LogKind.App, "cx")));
        Assert.Equal(["ax", "cx"], view.Rows.Select(r => r.Line.Text));
    }

    [Fact]
    public void ErrorsOnly_KeepsErrorLinesAndWholeGroups()
    {
        var view = new LogView();
        view.Append(Lines(1,
            (LogKind.App, "plain"),
            (LogKind.Error, "single error"),
            (LogKind.Error, DeviceHeader),
            (LogKind.Error, "a"),
            (LogKind.Error, Footer),
            (LogKind.Tool, "tool"),
            (LogKind.Reload, "reloaded")));

        view.SetErrorsOnly(true);

        Assert.Equal(["single error", DeviceHeader], view.Rows.Select(r => r.Line.Text));
        Assert.Equal(LogRowRole.GroupHeader, view.Rows[1].Role);

        view.Append(Lines(8, (LogKind.App, "later"), (LogKind.Error, "later error")));
        Assert.Equal("later error", view.Rows[^1].Line.Text);
        Assert.DoesNotContain(view.Rows, r => r.Line.Text == "later");
    }

    [Fact]
    public void ErrorsOnly_CombinedWithSearch_UsesGroupMembership()
    {
        var view = new LogView();
        view.Append(Lines(1,
            (LogKind.App, "foo plain"),
            (LogKind.Error, "foo error"),
            (LogKind.Error, DeviceHeader),
            (LogKind.Error, "foo inside"),
            (LogKind.Error, Footer)));

        view.SetErrorsOnly(true);
        view.SetSearch("foo");

        Assert.Equal(["foo error", "foo inside"], view.Rows.Select(r => r.Line.Text));
    }

    [Fact]
    public void FindError_NextAndPrevious_WrapAround()
    {
        var view = new LogView();
        view.Append(Lines(1,
            (LogKind.App, "0"),                // row 0
            (LogKind.Error, "e1"),             // row 1
            (LogKind.App, "2"),                // row 2
            (LogKind.Error, DeviceHeader),     // row 3 (group header)
            (LogKind.Error, "inner"),
            (LogKind.Error, Footer),
            (LogKind.App, "6"),                // row 4
            (LogKind.Error, "e2")));           // row 5

        Assert.Equal(1, view.FindError(-1, forward: true));
        Assert.Equal(3, view.FindError(1, forward: true));
        Assert.Equal(5, view.FindError(3, forward: true));
        Assert.Equal(1, view.FindError(5, forward: true));   // wraps

        Assert.Equal(5, view.FindError(-1, forward: false)); // no anchor: last
        Assert.Equal(3, view.FindError(5, forward: false));
        Assert.Equal(1, view.FindError(3, forward: false));
        Assert.Equal(5, view.FindError(1, forward: false));  // wraps
    }

    [Fact]
    public void FindError_ExpandedGroupChildrenAreNotTargets()
    {
        var view = new LogView();
        view.Append(Lines(1,
            (LogKind.Error, DeviceHeader),
            (LogKind.Error, "inner"),
            (LogKind.Error, Footer),
            (LogKind.App, "x"),
            (LogKind.Error, "e2")));
        view.SetExpanded(view.Entries[0], true); // rows: header, inner, footer, x, e2

        Assert.Equal(4, view.FindError(0, forward: true));
        Assert.Equal(0, view.FindError(4, forward: true));
        Assert.Equal(0, view.FindError(4, forward: false));
    }

    [Fact]
    public void FindError_NoErrors_ReturnsMinusOne()
    {
        var view = new LogView();
        view.Append(Lines(1, (LogKind.App, "a")));
        Assert.Equal(-1, view.FindError(-1, true));
        Assert.Equal(-1, view.FindError(0, false));
    }

    [Fact]
    public void Clear_HidesLinesFromViewOnly_BufferUntouched()
    {
        var buffer = new LogBuffer();
        var view = new LogView();
        buffer.Append(LogKind.App, "one");
        buffer.Append(LogKind.App, "two");
        view.Append(buffer.Snapshot());
        Assert.Equal(2, view.Rows.Count);

        view.Clear();
        Assert.Empty(view.Rows);
        Assert.Equal(0, view.TotalLines);
        Assert.Equal(2, buffer.Snapshot().Count);

        // a stale re-delivery of old lines is ignored; only new lines show
        buffer.Append(LogKind.App, "three");
        view.Append(buffer.Snapshot());
        Assert.Equal(["three"], view.Rows.Select(r => r.Line.Text));
        Assert.Equal(3, view.LastSeq);
        Assert.Equal(["three"], buffer.Since(2).Select(l => l.Text));
    }

    [Fact]
    public void Append_IgnoresLinesAtOrBelowLastSeq()
    {
        var view = new LogView();
        view.Append(Lines(5, (LogKind.App, "a"), (LogKind.App, "b")));
        view.Append(Lines(3, (LogKind.App, "old"), (LogKind.App, "older")));
        view.Append(Lines(6, (LogKind.App, "b-again"), (LogKind.App, "c")));
        Assert.Equal(["a", "b", "c"], view.Rows.Select(r => r.Line.Text));
        Assert.Equal(7, view.LastSeq);
    }

    [Fact]
    public void Capacity_EvictsOldestEntries_AndTheirRows()
    {
        var view = new LogView(capacity: 5);
        view.Append(Lines(1, (LogKind.App, "1"), (LogKind.Error, DeviceHeader), (LogKind.Error, "x"), (LogKind.Error, Footer), (LogKind.App, "5")));
        Assert.Equal(5, view.TotalLines);

        view.Append(Lines(6, (LogKind.App, "6")));

        Assert.Equal(5, view.TotalLines);
        Assert.Equal([DeviceHeader, "5", "6"], view.Rows.Select(r => r.Line.Text));

        view.Append(Lines(7, (LogKind.App, "7")));

        // the whole 3-line group is dropped at once
        Assert.Equal(["5", "6", "7"], view.Rows.Select(r => r.Line.Text));
        Assert.Equal(3, view.TotalLines);
    }

    [Fact]
    public void Capacity_LargeBulkAppend_StaysBounded()
    {
        var view = new LogView(capacity: 1000);
        view.Append(Enumerable.Range(1, 5000).Select(i => L(LogKind.App, "line " + i, i)).ToList());
        Assert.Equal(1000, view.TotalLines);
        Assert.Equal(1000, view.Rows.Count);
        Assert.Equal("line 4001", view.Rows[0].Line.Text);
        Assert.Equal("line 5000", view.Rows[^1].Line.Text);
    }

    [Fact]
    public void BulkAppend_ProducesSameRowsAsIncremental()
    {
        var all = FixtureLines();
        var bulk = new LogView();
        bulk.Append(all);
        var incremental = new LogView();
        foreach (var line in all)
        {
            incremental.Append([line]);
        }
        Assert.Equal(bulk.Rows.Select(r => (r.Line.Seq, r.Role)), incremental.Rows.Select(r => (r.Line.Seq, r.Role)));

        var big = Enumerable.Range(1, 3000).Select(i => L(LogKind.App, "n" + i, i)).ToList();
        var resets = 0;
        var view = new LogView();
        view.RowsReset += () => resets++;
        view.Append(big);
        Assert.Equal(3000, view.Rows.Count);
        Assert.True(resets >= 1);
    }

    [Fact]
    public void IndexOfSeq_ExpandsGroupAndFindsTheRow()
    {
        var view = new LogView();
        view.Append(Lines(1,
            (LogKind.App, "a"),
            (LogKind.Error, DeviceHeader),
            (LogKind.Error, "inner"),
            (LogKind.Error, Footer),
            (LogKind.App, "z")));

        Assert.Equal(0, view.IndexOfSeq(1, expand: true));
        Assert.Equal(2, view.IndexOfSeq(3, expand: true)); // "inner": group expands
        Assert.True(view.Entries[1].Expanded);
        Assert.Equal("inner", view.Rows[2].Line.Text);
        Assert.Equal(-1, view.IndexOfSeq(99, expand: true));
    }

    [Fact]
    public void IndexOfSeq_FilteredOut_ReturnsMinusOne()
    {
        var view = new LogView();
        view.Append(Lines(1, (LogKind.App, "a"), (LogKind.Error, "e")));
        view.SetErrorsOnly(true);
        Assert.Equal(-1, view.IndexOfSeq(1, expand: false));
        Assert.Equal(0, view.IndexOfSeq(2, expand: false));
    }

    [Fact]
    public void CopyText_Format_IsTimeThenTwoSpacesThenText()
    {
        var line = L(LogKind.App, "hello", 1);
        var text = LogView.FormatForCopy([line], TimeZoneInfo.Utc);
        Assert.Equal("10:20:30.456  hello", text);

        var two = LogView.FormatForCopy([line, L(LogKind.Error, "bad", 2)], TimeZoneInfo.Utc);
        Assert.Equal("10:20:30.456  hello" + Environment.NewLine + "10:20:30.456  bad", two);
        Assert.Equal(string.Empty, LogView.FormatForCopy([], TimeZoneInfo.Utc));
    }

    [Fact]
    public void GetCopyLines_NoSelection_IsEverythingWithGroupsFullyIncluded()
    {
        var view = new LogView();
        view.Append(Lines(1, (LogKind.App, "a"), (LogKind.Error, DeviceHeader), (LogKind.Error, "inner"), (LogKind.Error, Footer), (LogKind.App, "z")));

        Assert.Equal(["a", DeviceHeader, "inner", Footer, "z"], view.GetCopyLines([]).Select(l => l.Text));

        view.SetExpanded(view.Entries[1], true); // expanded rows must not duplicate the children
        Assert.Equal(["a", DeviceHeader, "inner", Footer, "z"], view.GetCopyLines([]).Select(l => l.Text));
    }

    [Fact]
    public void GetCopyLines_Selection_CollapsedHeaderCopiesWholeGroup_ExpandedOnlySelectedRows()
    {
        var view = new LogView();
        view.Append(Lines(1, (LogKind.App, "a"), (LogKind.Error, DeviceHeader), (LogKind.Error, "inner"), (LogKind.Error, Footer), (LogKind.App, "z")));

        var collapsed = view.GetCopyLines([view.Rows[1], view.Rows[2]]);
        Assert.Equal([DeviceHeader, "inner", Footer, "z"], collapsed.Select(l => l.Text));

        view.SetExpanded(view.Entries[1], true);
        var expanded = view.GetCopyLines([view.Rows[2], view.Rows[1]]); // order follows the list, not the selection
        Assert.Equal([DeviceHeader, "inner"], expanded.Select(l => l.Text));
    }

    [Fact]
    public void GetCopyLines_SearchActive_CopiesVisibleMatches()
    {
        var view = new LogView();
        view.Append(Lines(1, (LogKind.App, "foo"), (LogKind.App, "bar"), (LogKind.App, "foo2")));
        view.SetSearch("foo");
        Assert.Equal(["foo", "foo2"], view.GetCopyLines([]).Select(l => l.Text));
    }

    [Fact]
    public void LastRemovedFromFront_CountsEvictedRowsPerAppend()
    {
        var view = new LogView(capacity: 10);
        view.Append(Enumerable.Range(1, 10).Select(i => L(LogKind.App, "l" + i, i)).ToList());
        Assert.Equal(0, view.LastRemovedFromFront);

        view.Append(Lines(11, (LogKind.App, "a"), (LogKind.App, "b"), (LogKind.App, "c")));
        Assert.Equal(3, view.LastRemovedFromFront);
        Assert.Equal("l4", view.Rows[0].Line.Text);

        view.Append(Lines(14, (LogKind.App, "d")));
        Assert.Equal(1, view.LastRemovedFromFront);

        view.Append([]);
        Assert.Equal(1, view.LastRemovedFromFront); // nothing appended: untouched

        view.Append(Lines(15, (LogKind.App, "e")));
        view.Append(Lines(16, (LogKind.App, "f")));
        Assert.Equal(1, view.LastRemovedFromFront); // reset per append, not cumulative
    }

    [Fact]
    public void LastRemovedFromFront_CountsRowsNotLines_ForGroupsAndFilters()
    {
        var view = new LogView(capacity: 6);
        view.Append(Lines(1, (LogKind.Error, DeviceHeader), (LogKind.Error, "a"), (LogKind.Error, "b"), (LogKind.Error, Footer), (LogKind.App, "x"), (LogKind.App, "y")));
        Assert.Equal(3, view.Rows.Count); // collapsed group = 1 row

        view.Append(Lines(7, (LogKind.App, "z")));

        Assert.Equal(1, view.LastRemovedFromFront); // the 4-line group went, but it was a single row
        Assert.Equal(["x", "y", "z"], view.Rows.Select(r => r.Line.Text));
    }

    [Fact]
    public void LastRemovedFromFront_ZeroAfterRebuild_AndResettingFiresFirst()
    {
        var view = new LogView(capacity: 1000);
        view.Append(Enumerable.Range(1, 1000).Select(i => L(LogKind.App, "l" + i, i)).ToList());
        var oldRows = view.Rows;
        var seenOld = false;
        view.RowsResetting += () => seenOld = ReferenceEquals(view.Rows, oldRows);

        view.Append(Enumerable.Range(1001, 600).Select(i => L(LogKind.App, "l" + i, i)).ToList()); // > bulk threshold

        Assert.True(seenOld);
        Assert.Equal(0, view.LastRemovedFromFront);
        Assert.Equal(1000, view.Rows.Count);
        Assert.Equal("l601", view.Rows[0].Line.Text);
    }

    [Fact]
    public void LongBlock_IsSplitIntoContinuationGroups()
    {
        var view = new LogView();
        var lines = new List<LogLine> { L(LogKind.Error, DeviceHeader, 1) };
        for (var i = 0; i < LogView.MaxGroupLines + 100; i++)
        {
            lines.Add(L(LogKind.Error, "frame " + i, 2 + i));
        }
        lines.Add(L(LogKind.Error, Footer, 2 + LogView.MaxGroupLines + 100));
        lines.Add(L(LogKind.App, "after", 3 + LogView.MaxGroupLines + 100));
        view.Append(lines);

        var groups = view.Entries.Where(e => e.IsGroup).ToList();
        Assert.Equal(2, groups.Count);
        Assert.False(groups[0].IsContinuation);
        Assert.True(groups[1].IsContinuation);
        Assert.Equal(LogView.MaxGroupLines, groups[0].LineCount);
        Assert.Equal(lines.Count - 1 - LogView.MaxGroupLines, groups[1].LineCount); // all but "after"
        Assert.True(groups[1].Closed);
        Assert.StartsWith("(lanjutan) frame", view.Rows.First(r => r.Entry == groups[1]).Text, StringComparison.Ordinal);
        Assert.Equal(DeviceHeader, view.Rows[0].Text);
        Assert.Equal("after", view.Rows[^1].Text);
    }

    [Fact]
    public void LongBlock_SplitIsTheSameIncrementally()
    {
        var lines = new List<LogLine> { L(LogKind.Error, DeviceHeader, 1) };
        for (var i = 0; i < 1200; i++)
        {
            lines.Add(L(LogKind.Error, "frame " + i, 2 + i));
        }
        var bulk = new LogView();
        bulk.Append(lines);
        var inc = new LogView();
        foreach (var l in lines)
        {
            inc.Append([l]);
        }
        Assert.Equal(bulk.Entries.Select(e => (e.LineCount, e.IsContinuation)), inc.Entries.Select(e => (e.LineCount, e.IsContinuation)));
        Assert.Equal(3, inc.Entries.Count);
        Assert.False(inc.Entries[^1].Closed);
    }

    [Fact]
    public void Capacity_HoldsEvenWithOneHugeOpenBlock()
    {
        var view = new LogView(capacity: 2000);
        var seq = 0L;
        view.Append([L(LogKind.Error, DeviceHeader, ++seq)]);
        for (var batch = 0; batch < 30; batch++)
        {
            view.Append(Enumerable.Range(0, 200).Select(_ => L(LogKind.Error, "frame", ++seq)).ToList());
            Assert.True(view.TotalLines <= 2000, "batch " + batch + ": " + view.TotalLines);
        }
        Assert.True(view.TotalLines <= 2000);
        Assert.Equal(view.Rows.Count, view.Entries.Count(e => e.IsGroup) + view.Entries.Count(e => !e.IsGroup));
    }
}
