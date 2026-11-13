namespace StatefulBar.Core.Projects;

internal static class PathKey
{
    /// <summary>Full path without trailing separator; compare results with OrdinalIgnoreCase.</summary>
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
