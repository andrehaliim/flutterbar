using FlutterBar.Core.Logging;

namespace FlutterBar.Core.Tests.Logging;

public class LogBufferTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Append_AssignsIncreasingSeq()
    {
        var buf = new LogBuffer();
        var a = buf.Append(LogKind.App, "a");
        var b = buf.Append(LogKind.Tool, "b");
        Assert.True(b.Seq > a.Seq);
        Assert.Equal("b", b.Text);
        Assert.Equal(LogKind.Tool, b.Kind);
    }

    [Fact]
    public void OverCapacity_DropsOldest_KeepsSeq()
    {
        var buf = new LogBuffer(3);
        for (var i = 1; i <= 5; i++)
        {
            buf.Append(LogKind.App, "l" + i);
        }
        var snap = buf.Snapshot();
        Assert.Equal([3L, 4L, 5L], snap.Select(l => l.Seq).ToArray());
        Assert.Equal(["l3", "l4", "l5"], snap.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void SinceLastReload_ReturnsLinesAfterMarker()
    {
        var buf = new LogBuffer();
        buf.MarkSessionStart("Sesi");
        buf.Append(LogKind.App, "before");
        buf.MarkReload("Hot reload");
        buf.Append(LogKind.App, "after1");
        buf.Append(LogKind.Error, "after2");
        Assert.Equal(["after1", "after2"], buf.SinceLastReload().Select(l => l.Text).ToArray());
    }

    [Fact]
    public void SinceLastReload_NoReload_ReturnsSinceSessionStart()
    {
        var buf = new LogBuffer();
        buf.Append(LogKind.App, "old");
        buf.MarkSessionStart("Sesi");
        buf.Append(LogKind.App, "new1");
        buf.Append(LogKind.App, "new2");
        Assert.Equal(["new1", "new2"], buf.SinceLastReload().Select(l => l.Text).ToArray());
    }

    [Fact]
    public void SinceLastReload_MarkerEvicted_ReturnsRemaining()
    {
        var buf = new LogBuffer(2);
        buf.MarkReload("Hot reload");
        buf.Append(LogKind.App, "x");
        buf.Append(LogKind.App, "y");
        Assert.Equal(["x", "y"], buf.SinceLastReload().Select(l => l.Text).ToArray());
    }

    [Fact]
    public void MarkReload_AppendsSeparatorWithLocalTime()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 11, 6, 3, 42, 15, TimeSpan.Zero));
        var buf = new LogBuffer(time: time);
        buf.MarkReload("Hot reload");
        var line = Assert.Single(buf.Snapshot());
        Assert.Equal(LogKind.Separator, line.Kind);
        Assert.Equal($"── Hot reload {time.GetLocalNow():HH:mm:ss} ──", line.Text);
    }

    [Fact]
    public void Since_ReturnsLinesAfterSeq()
    {
        var buf = new LogBuffer();
        var a = buf.Append(LogKind.App, "a");
        buf.Append(LogKind.App, "b");
        Assert.Equal(["b"], buf.Since(a.Seq).Select(l => l.Text).ToArray());
        Assert.Equal(2, buf.Since(0).Count);
    }

    [Fact]
    public void Tail_ReturnsLastN()
    {
        var buf = new LogBuffer();
        for (var i = 1; i <= 5; i++)
        {
            buf.Append(LogKind.App, "l" + i);
        }
        Assert.Equal(["l4", "l5"], buf.Tail(2).Select(l => l.Text).ToArray());
        Assert.Equal(5, buf.Tail(100).Count);
        Assert.Empty(buf.Tail(0));
    }

    [Fact]
    public void Appended_RaisedOutsideLock()
    {
        var buf = new LogBuffer();
        var seen = new List<string>();
        buf.Appended += l => seen.Add(buf.Snapshot().Count + ":" + l.Text); // re-enters buffer
        buf.Append(LogKind.App, "a");
        Assert.Equal(["1:a"], seen);
    }

    [Fact]
    public void ConcurrentAppend_NoLostLines()
    {
        var buf = new LogBuffer(4000);
        var threads = Enumerable.Range(0, 4)
            .Select(t => new Thread(() =>
            {
                for (var i = 0; i < 1000; i++)
                {
                    buf.Append(LogKind.App, $"t{t}-{i}");
                }
            }))
            .ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        var snap = buf.Snapshot();
        Assert.Equal(4000, snap.Count);
        Assert.Equal(4000, snap.Select(l => l.Seq).Distinct().Count());
    }
}
