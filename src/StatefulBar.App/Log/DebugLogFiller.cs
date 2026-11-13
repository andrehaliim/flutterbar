#if DEBUG
using FlutterBar.Core.Logging;

namespace FlutterBar.App.Log;

/// <summary>Debug-only: writes dummy lines into the real <see cref="LogBuffer"/> (Task 15 manual check). Not in Release.</summary>
internal static class DebugLogFiller
{
    private const string WebHeader = "══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══════════════════════════════════════════════════════════";

    /// <summary>Set by <c>--gc-on-log-close</c>: closing the LogWindow forces a GC and logs whether the window was collected.</summary>
    public static bool GcOnLogClose { get; set; }

    private static int s_streaming;

    /// <summary>Appends <paramref name="perTick"/> dummy lines every 50 ms until the process exits (idempotent).</summary>
    public static void StreamInBackground(LogBuffer buffer, int perTick)
    {
        if (Interlocked.Exchange(ref s_streaming, 1) == 1)
        {
            return;
        }
        Task.Run(async () =>
        {
            long n = 0;
            while (true)
            {
                for (var i = 0; i < perTick; i++)
                {
                    buffer.Append(LogKind.App, $"I/flutter (stream): streamed line {++n}");
                }
                await Task.Delay(50).ConfigureAwait(false);
            }
        });
    }

    /// <summary>After the LogWindow closed: GC twice, then log managed/private bytes and whether the window died.</summary>
    public static void ForceGcAndLog(WeakReference window, WeakReference viewModel, WeakReference list)
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
        var p = System.Diagnostics.Process.GetCurrentProcess();
        FlutterBar.App.Diagnostics.AppLog.Write(
            $"DEBUG gc-on-log-close: windowAlive={window.IsAlive} viewModelAlive={viewModel.IsAlive} listBoxAlive={list.IsAlive} managedMB={GC.GetTotalMemory(true) / 1048576.0:F1} privateMB={p.PrivateMemorySize64 / 1048576.0:F1} workingSetMB={p.WorkingSet64 / 1048576.0:F1}");
    }

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
