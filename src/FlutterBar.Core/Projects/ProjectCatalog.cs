using FlutterBar.Core.Settings;

namespace FlutterBar.Core.Projects;

public sealed record ProjectInfo(string Path, string DisplayName, bool Available, DateTimeOffset? LastUsed);

public static class ProjectCatalog
{
    /// <summary>(scanned + manual) - hidden, ordered by LastUsed (newest first) then name.</summary>
    public static IReadOnlyList<ProjectInfo> Build(
        IReadOnlyList<string> scanned, AppSettings settings, Func<string, bool> directoryExists)
    {
        var hidden = new HashSet<string>(settings.HiddenProjects.Select(PathKey.Normalize), StringComparer.OrdinalIgnoreCase);

        var lastUsed = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, state) in settings.Projects)
        {
            lastUsed[PathKey.Normalize(key)] = state.LastUsed;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in scanned.Concat(settings.ManualProjects))
        {
            var path = PathKey.Normalize(raw);
            if (!hidden.Contains(path))
            {
                paths.Add(path);
            }
        }

        var duplicateNames = paths
            .GroupBy(FolderName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. paths
                .Select(p => new ProjectInfo(
                    p,
                    DisplayName(p, duplicateNames),
                    directoryExists(p),
                    lastUsed.GetValueOrDefault(p)))
                .OrderByDescending(p => p.LastUsed.HasValue)
                .ThenByDescending(p => p.LastUsed)
                .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static string FolderName(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name.Length > 0 ? name : path;
    }

    private static string DisplayName(string path, HashSet<string> duplicateNames)
    {
        var name = FolderName(path);
        if (!duplicateNames.Contains(name))
        {
            return name;
        }
        var parent = System.IO.Path.GetDirectoryName(path);
        var parentName = parent is null ? "" : FolderName(parent);
        return parentName.Length > 0 ? $"{name} ({parentName})" : name;
    }
}
