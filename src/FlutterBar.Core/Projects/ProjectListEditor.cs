using FlutterBar.Core.Settings;

namespace FlutterBar.Core.Projects;

/// <summary>
/// Edits <c>scanRoots</c>, <c>manualProjects</c> and <c>hiddenProjects</c> (spec §4.3) for the "Kelola project" dialog.
/// Paths are stored normalized (<see cref="PathKey"/>) and compared case-insensitively, so hand-written variants
/// (<c>C:\Work\</c>, <c>c:\work</c>) count as one entry. Each method returns whether the list changed.
/// </summary>
public static class ProjectListEditor
{
    public static bool AddScanRoot(AppSettings settings, string folder) => Add(NotNull(settings).ScanRoots, folder);

    public static bool RemoveScanRoot(AppSettings settings, string folder) => Remove(NotNull(settings).ScanRoots, folder);

    public static bool AddManual(AppSettings settings, string folder) => Add(NotNull(settings).ManualProjects, folder);

    public static bool RemoveManual(AppSettings settings, string folder) => Remove(NotNull(settings).ManualProjects, folder);

    public static bool Hide(AppSettings settings, string project) => Add(NotNull(settings).HiddenProjects, project);

    public static bool Unhide(AppSettings settings, string project) => Remove(NotNull(settings).HiddenProjects, project);

    private static AppSettings NotNull(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings;
    }

    private static bool Add(List<string> list, string path)
    {
        var key = Key(path);
        if (list.Any(p => Same(p, key)))
        {
            return false;
        }
        list.Add(key);
        return true;
    }

    private static bool Remove(List<string> list, string path)
    {
        var key = Key(path);
        return list.RemoveAll(p => Same(p, key)) > 0;
    }

    private static string Key(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Path folder harus absolut: \"{path}\"", nameof(path));
        }
        return PathKey.Normalize(path);
    }

    // Entries written by hand may be blank or relative: they never match, and are left alone.
    private static bool Same(string entry, string key) =>
        !string.IsNullOrWhiteSpace(entry) && Path.IsPathFullyQualified(entry)
        && string.Equals(PathKey.Normalize(entry), key, StringComparison.OrdinalIgnoreCase);
}
