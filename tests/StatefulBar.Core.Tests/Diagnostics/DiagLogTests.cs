using StatefulBar.Core.Diagnostics;

namespace StatefulBar.Core.Tests.Diagnostics;

[Collection("DiagLog")]
public sealed class DiagLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "StatefulBarDiag-" + Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_dir, "diag.log");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.WriteLine("test cleanup failed: " + ex.Message);
        }
    }

    [Fact]
    public void Write_AppendsTimestampAndException()
    {
        DiagLog.Initialize(LogPath);
        DiagLog.Write("hello", new InvalidOperationException("boom"));
        var text = File.ReadAllText(LogPath);
        Assert.Contains("hello", text);
        Assert.Contains("InvalidOperationException: boom", text);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", text);
    }

    [Fact]
    public void Write_RotatesWhenOverMax()
    {
        DiagLog.Initialize(LogPath, maxBytes: 200);
        DiagLog.Write(new string('a', 300));
        DiagLog.Write("second");
        Assert.True(File.Exists(LogPath + ".1"));
        Assert.Contains(new string('a', 300), File.ReadAllText(LogPath + ".1"));
        var current = File.ReadAllText(LogPath);
        Assert.Contains("second", current);
        Assert.DoesNotContain(new string('a', 300), current);
    }

    [Fact]
    public void Write_ConcurrentCalls_NoException()
    {
        DiagLog.Initialize(LogPath, maxBytes: 5_000);
        var ex = Record.Exception(() => Parallel.For(0, 8, t =>
        {
            for (var i = 0; i < 100; i++)
            {
                DiagLog.Write($"t{t}-{i}", new InvalidOperationException("x"));
            }
        }));
        Assert.Null(ex);
        Assert.True(File.Exists(LogPath));
    }
}
