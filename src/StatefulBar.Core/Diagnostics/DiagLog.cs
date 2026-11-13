using System.Diagnostics;

namespace StatefulBar.Core.Diagnostics;

/// <summary>
/// Last-resort error sink writing to <c>diag.log</c>. Never throws to callers: if the file cannot be
/// written, the entry goes to <see cref="Trace"/> instead (there is nowhere else left to report it).
/// </summary>
public static class DiagLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private static long _maxBytes = 1_000_000;

    public static void Initialize(string path, long maxBytes = 1_000_000)
    {
        lock (Gate)
        {
            _path = path;
            _maxBytes = maxBytes;
        }
    }

    public static void Write(string message, Exception? ex = null)
    {
        var entry = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"
            + (ex is null ? string.Empty : ex + Environment.NewLine);
        lock (Gate)
        {
            try
            {
                var path = _path ??= DefaultPath();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                Rotate(path);
                File.AppendAllText(path, entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"DiagLog write failed ({e.Message}): {entry}");
            }
        }
    }

    private static void Rotate(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length > _maxBytes)
        {
            File.Move(path, path + ".1", overwrite: true);
        }
    }

    private static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StatefulBar", "diag.log");
}
