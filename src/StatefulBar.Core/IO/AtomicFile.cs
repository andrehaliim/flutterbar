using System.Text;

namespace StatefulBar.Core.IO;

internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Writes <paramref name="contents"/> to "path.tmp" and then moves it over <paramref name="path"/>.
    /// Failures propagate to the caller; the temp file is removed if it was left behind.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents, Utf8NoBom);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }
}
