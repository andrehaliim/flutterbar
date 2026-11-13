namespace FlutterBar.Core.Projects;

public static class ProjectScanner
{
    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".dart_tool", "build", "node_modules", "android", "ios", "windows", "linux", "macos", "web",
    };

    /// <summary>
    /// Best-effort scan: a missing root is ignored, and a folder that cannot be read (UnauthorizedAccessException, or an
    /// IOException such as an offline OneDrive placeholder, a dead junction or a locked pubspec.yaml) is skipped and
    /// reported to <paramref name="onSkipped"/> (path, error); the rest of the scan goes on.
    /// </summary>
    public static IReadOnlyList<string> Scan(
        IEnumerable<string> roots, int maxDepth = 3, Action<string, Exception>? onSkipped = null)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var full = PathKey.Normalize(root);
            if (Directory.Exists(full))
            {
                Visit(full, 0, maxDepth, found, onSkipped);
            }
        }
        return [.. found.Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static void Visit(string dir, int depth, int maxDepth, HashSet<string> found, Action<string, Exception>? onSkipped)
    {
        try
        {
            var pubspec = Path.Combine(dir, "pubspec.yaml");
            if (File.Exists(pubspec) && PubspecInspector.IsFlutterApp(File.ReadAllText(pubspec)))
            {
                found.Add(dir);
                return;
            }

            if (depth >= maxDepth)
            {
                return;
            }

            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || ExcludedFolders.Contains(name))
                {
                    continue;
                }
                Visit(child, depth + 1, maxDepth, found, onSkipped);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Skip this folder (and what is left of it); reported to the caller, which writes diag.log.
            onSkipped?.Invoke(dir, ex);
        }
    }
}
