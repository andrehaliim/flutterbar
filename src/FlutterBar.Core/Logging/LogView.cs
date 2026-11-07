using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace FlutterBar.Core.Logging;

/// <summary>How a <see cref="LogRow"/> relates to its <see cref="LogEntry"/>.</summary>
public enum LogRowRole
{
    /// <summary>A plain line (also every line while a search filter flattens the groups).</summary>
    Line,

    /// <summary>First line of an exception block; stands for the whole block while it is collapsed.</summary>
    GroupHeader,

    /// <summary>A line of an expanded exception block (everything after the header).</summary>
    GroupChild,
}

/// <summary>
/// A single log line, or an exception block (header through footer) shown as one collapsible item. Created and
/// mutated only by <see cref="LogView"/> on the thread that owns it.
/// </summary>
public sealed class LogEntry : INotifyPropertyChanged
{
    private readonly List<LogLine> _lines;
    private bool _expanded;

    internal LogEntry(LogLine first, bool isGroup, bool isContinuation = false)
    {
        _lines = [first];
        IsGroup = isGroup;
        IsContinuation = isContinuation;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsGroup { get; }

    /// <summary>A block that was split because it exceeded <see cref="LogView.MaxGroupLines"/>: continues the previous group.</summary>
    public bool IsContinuation { get; }

    /// <summary>True once the footer arrived (or the block ended some other way); only groups can be open.</summary>
    public bool Closed { get; internal set; }

    public bool Expanded
    {
        get => _expanded;
        internal set
        {
            if (_expanded != value)
            {
                _expanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expanded)));
            }
        }
    }

    public IReadOnlyList<LogLine> Lines => _lines;

    public LogLine Header => _lines[0];

    public int LineCount => _lines.Count;

    internal bool Contains(long seq) => seq >= _lines[0].Seq && seq <= _lines[^1].Seq;

    internal void Add(LogLine line)
    {
        _lines.Add(line);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineCount)));
    }
}

/// <summary>One row of the list: reference identity matters (the UI keeps selections of rows).</summary>
public sealed class LogRow : INotifyPropertyChanged
{
    internal LogRow(LogEntry entry, LogLine line, LogRowRole role)
    {
        Entry = entry;
        Line = line;
        Role = role;
    }

    /// <summary>
    /// Rows are immutable, so this never fires. Implementing the interface (with no-op accessors) stops WPF from hooking
    /// the static property-descriptor change tables for bindings to a plain CLR object, which would keep every closed
    /// LogWindow (through its row bindings) alive.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }

    public LogEntry Entry { get; }

    public LogLine Line { get; }

    public LogRowRole Role { get; }

    public bool IsHeader => Role == LogRowRole.GroupHeader;

    /// <summary>Display text: the line, with a "(continued)" prefix on the header of a split block.</summary>
    public string Text => Role == LogRowRole.GroupHeader && Entry.IsContinuation ? "(continued) " + Line.Text : Line.Text;
}

/// <summary>
/// Pure model behind the LogWindow list (spec §8): groups exception blocks into collapsible entries, applies the
/// search / errors-only filters, and keeps <see cref="Rows"/> in sync incrementally. Not thread-safe: drive it from one
/// thread (the UI thread). Lines must be fed in sequence order; anything at or below <see cref="LastSeq"/> is ignored.
/// </summary>
public sealed class LogView
{
    /// <summary>Above this many new lines a single append rebuilds the rows instead of notifying row by row.</summary>
    private const int BulkThreshold = 500;

    /// <summary>An exception block longer than this is split into a new group so one open block can never outgrow the capacity.</summary>
    public const int MaxGroupLines = 500;

    private readonly int _capacity;
    private readonly List<LogEntry> _entries = [];
    private LogEntry? _open;
    private string _search = string.Empty;
    private bool _errorsOnly;

    public LogView(int capacity = 20_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        Rows = [];
    }

    /// <summary>The collection is replaced (not cleared) on a rebuild; this is raised after the new one is in place.</summary>
    public event Action? RowsReset;

    /// <summary>Raised just before the rows are rebuilt, while the old <see cref="Rows"/> are still in place (UI saves its scroll/selection).</summary>
    public event Action? RowsResetting;

    public ObservableCollection<LogRow> Rows { get; private set; }

    /// <summary>Rows removed from the front of <see cref="Rows"/> by the last <see cref="Append"/> (capacity eviction); 0 after a rebuild.</summary>
    public int LastRemovedFromFront { get; private set; }

    public IReadOnlyList<LogEntry> Entries => _entries;

    /// <summary>Highest sequence number ever appended, including lines hidden by <see cref="Clear"/>.</summary>
    public long LastSeq { get; private set; }

    /// <summary>Lines currently held (hidden or not by the filters); bounded by the capacity.</summary>
    public int TotalLines { get; private set; }

    public string SearchText => _search;

    public bool ErrorsOnly => _errorsOnly;

    public void Append(IReadOnlyList<LogLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }
        LastRemovedFromFront = 0;
        var bulk = lines.Count > BulkThreshold;
        foreach (var line in lines)
        {
            if (line.Seq <= LastSeq)
            {
                continue;
            }
            LastSeq = line.Seq;
            AddLine(line, bulk);
        }
        Trim(bulk);
        if (bulk)
        {
            Rebuild();
        }
    }

    /// <summary>Hides everything held so far from the view. The source buffer is not touched.</summary>
    public void Clear()
    {
        _entries.Clear();
        _open = null;
        TotalLines = 0;
        Rebuild();
    }

    public void SetSearch(string? text)
    {
        var value = text ?? string.Empty;
        if (value == _search)
        {
            return;
        }
        _search = value;
        Rebuild();
    }

    public void SetErrorsOnly(bool on)
    {
        if (on == _errorsOnly)
        {
            return;
        }
        _errorsOnly = on;
        Rebuild();
    }

    public void SetExpanded(LogEntry entry, bool expanded)
    {
        if (!entry.IsGroup || entry.Expanded == expanded)
        {
            return;
        }
        entry.Expanded = expanded;
        if (IsFlat)
        {
            return; // groups are flattened while searching; the flag applies once the search is cleared
        }
        var header = IndexOfHeader(entry);
        if (header < 0)
        {
            return;
        }
        if (expanded)
        {
            for (var i = 1; i < entry.LineCount; i++)
            {
                Rows.Insert(header + i, new LogRow(entry, entry.Lines[i], LogRowRole.GroupChild));
            }
        }
        else
        {
            while (header + 1 < Rows.Count && Rows[header + 1].Role == LogRowRole.GroupChild && ReferenceEquals(Rows[header + 1].Entry, entry))
            {
                Rows.RemoveAt(header + 1);
            }
        }
    }

    /// <summary>
    /// Row index showing <paramref name="seq"/> (its group is expanded when <paramref name="expand"/> is set), or -1 when
    /// the line is unknown or filtered out.
    /// </summary>
    public int IndexOfSeq(long seq, bool expand)
    {
        var entry = FindEntry(seq);
        if (entry is null)
        {
            return -1;
        }
        if (expand && entry.IsGroup)
        {
            SetExpanded(entry, true);
        }
        var byEntry = -1;
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            if (row.Line.Seq == seq)
            {
                return i;
            }
            if (byEntry < 0 && ReferenceEquals(row.Entry, entry))
            {
                byEntry = i; // collapsed group: its header stands for the line
            }
        }
        return byEntry;
    }

    /// <summary>
    /// Next/previous error row relative to <paramref name="anchorRow"/> (-1 = no anchor), wrapping around. Targets are
    /// group headers and error lines, never the children of an expanded group. -1 when there is none.
    /// </summary>
    public int FindError(int anchorRow, bool forward)
    {
        var n = Rows.Count;
        if (n == 0)
        {
            return -1;
        }
        var start = forward
            ? Math.Min(anchorRow + 1, n)
            : (anchorRow < 0 ? n - 1 : Math.Min(anchorRow - 1, n - 1));
        for (var k = 0; k < n; k++)
        {
            var i = forward ? (start + k) % n : (((start - k) % n) + n) % n;
            if (IsErrorTarget(Rows[i]))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Lines to copy: the selected rows, or every row when nothing is selected. A collapsed group header stands for its
    /// whole block; with no selection every block is included in full. Order follows the list.
    /// </summary>
    public IReadOnlyList<LogLine> GetCopyLines(IEnumerable<LogRow> selected)
    {
        var chosen = new HashSet<LogRow>(selected);
        var all = chosen.Count == 0;
        var result = new List<LogLine>();
        foreach (var row in Rows)
        {
            if (all)
            {
                if (row.Role == LogRowRole.GroupChild)
                {
                    continue;
                }
                AddRowLines(result, row, wholeGroup: true);
            }
            else if (chosen.Contains(row))
            {
                AddRowLines(result, row, wholeGroup: !row.Entry.Expanded);
            }
        }
        return result;
    }

    /// <summary><c>HH:mm:ss.fff  text</c> per line (local time unless <paramref name="zone"/> is given), one per line.</summary>
    public static string FormatForCopy(IEnumerable<LogLine> lines, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        return string.Join(
            Environment.NewLine,
            lines.Select(l => $"{TimeZoneInfo.ConvertTime(l.Timestamp, zone).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {l.Text}"));
    }

    private bool IsFlat => _search.Length > 0;

    private static void AddRowLines(List<LogLine> result, LogRow row, bool wholeGroup)
    {
        if (row.IsHeader && wholeGroup)
        {
            result.AddRange(row.Entry.Lines);
        }
        else
        {
            result.Add(row.Line);
        }
    }

    private static bool IsErrorTarget(LogRow row) =>
        row.Role == LogRowRole.GroupHeader || (row.Role == LogRowRole.Line && row.Line.Kind == LogKind.Error);

    private bool Matches(LogEntry entry, LogLine line) =>
        (!_errorsOnly || entry.IsGroup || line.Kind == LogKind.Error)
        && (_search.Length == 0 || line.Text.Contains(_search, StringComparison.OrdinalIgnoreCase));

    private void AddLine(LogLine line, bool bulk)
    {
        TotalLines++;
        if (_open is { } open)
        {
            var isFooter = LogClassifier.IsExceptionFooter(line.Text);
            if (line.Kind == LogKind.Error && !LogClassifier.IsExceptionHeader(line.Text)
                && (open.LineCount < MaxGroupLines || isFooter))
            {
                open.Add(line);
                if (isFooter)
                {
                    open.Closed = true;
                    _open = null;
                }
                if (!bulk)
                {
                    AddChildRow(open, line);
                }
                return;
            }
            // Ended without a footer (anything that is not part of the dump), or too long: split and continue.
            var continues = line.Kind == LogKind.Error && !LogClassifier.IsExceptionHeader(line.Text);
            open.Closed = true;
            _open = null;
            if (continues)
            {
                StartEntry(new LogEntry(line, isGroup: true, isContinuation: true), bulk);
                return;
            }
        }

        var isGroup = line.Kind != LogKind.Separator && LogClassifier.IsExceptionHeader(line.Text);
        StartEntry(new LogEntry(line, isGroup), bulk);
    }

    private void StartEntry(LogEntry entry, bool bulk)
    {
        var line = entry.Header;
        var isGroup = entry.IsGroup;
        _entries.Add(entry);
        if (isGroup)
        {
            _open = entry;
        }
        if (bulk)
        {
            return;
        }
        if (IsFlat)
        {
            if (Matches(entry, line))
            {
                Rows.Add(new LogRow(entry, line, LogRowRole.Line));
            }
        }
        else if (isGroup)
        {
            Rows.Add(new LogRow(entry, line, LogRowRole.GroupHeader));
        }
        else if (!_errorsOnly || line.Kind == LogKind.Error)
        {
            Rows.Add(new LogRow(entry, line, LogRowRole.Line));
        }
    }

    // A line joined an open group: add its row when it is visible (expanded group, or flat mode).
    private void AddChildRow(LogEntry group, LogLine line)
    {
        if (IsFlat)
        {
            if (Matches(group, line))
            {
                Rows.Add(new LogRow(group, line, LogRowRole.Line));
            }
            return;
        }
        if (!group.Expanded)
        {
            return;
        }
        var header = IndexOfHeader(group);
        if (header >= 0)
        {
            Rows.Insert(header + group.LineCount - 1, new LogRow(group, line, LogRowRole.GroupChild));
        }
    }

    private void Trim(bool bulk)
    {
        var drop = 0;
        var total = TotalLines;
        while (total > _capacity && drop < _entries.Count)
        {
            total -= _entries[drop].LineCount;
            drop++;
        }
        if (drop == 0)
        {
            return;
        }
        var removed = _entries.GetRange(0, drop);
        _entries.RemoveRange(0, drop);
        TotalLines = total;
        if (_open is not null && removed.Contains(_open))
        {
            _open = null; // only reachable when the open block alone exceeds a tiny capacity
        }
        if (bulk)
        {
            return; // the caller rebuilds
        }
        // Visible rows of the oldest entries sit at the very front.
        var gone = new HashSet<LogEntry>(removed);
        while (Rows.Count > 0 && gone.Contains(Rows[0].Entry))
        {
            Rows.RemoveAt(0);
            LastRemovedFromFront++;
        }
    }

    private void Rebuild()
    {
        RowsResetting?.Invoke();
        LastRemovedFromFront = 0;
        var rows = new List<LogRow>();
        foreach (var entry in _entries)
        {
            if (IsFlat)
            {
                foreach (var line in entry.Lines)
                {
                    if (Matches(entry, line))
                    {
                        rows.Add(new LogRow(entry, line, LogRowRole.Line));
                    }
                }
            }
            else if (entry.IsGroup)
            {
                rows.Add(new LogRow(entry, entry.Header, LogRowRole.GroupHeader));
                if (entry.Expanded)
                {
                    for (var i = 1; i < entry.LineCount; i++)
                    {
                        rows.Add(new LogRow(entry, entry.Lines[i], LogRowRole.GroupChild));
                    }
                }
            }
            else if (!_errorsOnly || entry.Header.Kind == LogKind.Error)
            {
                rows.Add(new LogRow(entry, entry.Header, LogRowRole.Line));
            }
        }
        Rows = new ObservableCollection<LogRow>(rows);
        RowsReset?.Invoke();
    }

    private int IndexOfHeader(LogEntry entry)
    {
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (Rows[i].Role == LogRowRole.GroupHeader && ReferenceEquals(Rows[i].Entry, entry))
            {
                return i;
            }
        }
        return -1;
    }

    private LogEntry? FindEntry(long seq)
    {
        int lo = 0, hi = _entries.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var entry = _entries[mid];
            if (seq < entry.Header.Seq)
            {
                hi = mid - 1;
            }
            else if (!entry.Contains(seq))
            {
                lo = mid + 1;
            }
            else
            {
                return entry;
            }
        }
        return null;
    }
}
