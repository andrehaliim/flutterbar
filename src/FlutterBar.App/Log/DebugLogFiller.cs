#if DEBUG
using FlutterBar.Core.Logging;

namespace FlutterBar.App.Log;

/// <summary>Debug-only: writes dummy lines into the real <see cref="LogBuffer"/> (Task 15 manual check). Not in Release.</summary>
internal static class DebugLogFiller
{
    private const string WebHeader = "══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══════════════════════════════════════════════════════════";

    public static void FillInBackground(LogBuffer buffer, int count) => Task.Run(() => Fill(buffer, count));

    public static void Fill(LogBuffer buffer, int count)
    {
        var written = 0;
        var i = 0;
        while (written < count)
        {
            i++;
            if (i % 997 == 0)
            {
                written += WriteException(buffer, i, count - written);
            }
            else if (i % 250 == 0)
            {
                buffer.Append(LogKind.Error, $"E/flutter ({i}): single error line {i}");
                written++;
            }
            else if (i % 100 == 0)
            {
                buffer.Append(LogKind.Reload, $"Reloaded {i % 7} of 1234 libraries in {i % 300} ms.");
                written++;
            }
            else if (i % 7 == 0)
            {
                buffer.Append(LogKind.Tool, $"Syncing files to device sdk gphone64 x86 64... {i}");
                written++;
            }
            else
            {
                var pad = i % 13 == 0 ? " — " + new string('x', 160) : string.Empty;
                buffer.Append(LogKind.App, $"I/flutter ({i}): dummy log line {i} lorem ipsum dolor sit amet{pad}");
                written++;
            }
        }
    }

    private static int WriteException(LogBuffer buffer, int i, int room)
    {
        var lines = new List<string> { WebHeader, $"The following StateError was thrown building Dummy({i}):", "Bad state: boom", string.Empty };
        for (var f = 0; f < 12; f++)
        {
            lines.Add($"packages/flutter/src/widgets/framework.dart {5000 + f}:7   frame{f}");
        }
        lines.Add(new string('═', 90));
        var n = Math.Min(room, lines.Count);
        for (var k = 0; k < n; k++)
        {
            buffer.Append(LogKind.Error, lines[k]);
        }
        return n;
    }
}
#endif
