namespace FlutterBar.Core.Projects;

public static class ProjectScanner
{
    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".dart_tool", "build", "node_modules", "android", "ios", "windows", "linux", "macos", "web",
    };

    /// <summary>
    /// Best-effort scan: a missing root is ignored and a subfolder that cannot be read
    /// (UnauthorizedAccessException) is skipped. Controller ruling: skipped paths are not recorded
    /// because DiagLog does not exist yet.
    /// </summary>
    public static IReadOnlyList<string> Scan(IEnumerable<string> roots, int maxDepth = 3)
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
                Visit(full, 0, maxDepth, found);
            }
        }
        return [.. found.Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static void Visit(string dir, int depth, int maxDepth, HashSet<string> found)
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
                Visit(child, depth + 1, maxDepth, found);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip unreadable folders; see the controller ruling in the Scan summary.
        }
    }
}
