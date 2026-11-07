using Microsoft.Win32;

namespace FlutterBar.Core.Integration;

public enum PathAddOutcome
{
    Added,
    AlreadyPresent,
}

/// <summary>
/// "Tambahkan CLI ke PATH" (spec §7.4): appends a folder to the user PATH (<c>HKCU\Environment\Path</c>) only when it is
/// not there yet. The existing text is kept verbatim (no reordering, no truncation, empty segments and <c>%VAR%</c> kept
/// unexpanded) and the value kind is preserved; a missing value is created as REG_EXPAND_SZ.
/// </summary>
public static class UserPathEditor
{
    public const string SubKey = "Environment";
    public const string ValueName = "Path";

    /// <summary>True when one segment of <paramref name="path"/> is <paramref name="folder"/>: case-insensitive, ignoring
    /// surrounding quotes/whitespace and a trailing separator, compared both as written and environment-expanded.</summary>
    public static bool Contains(string? path, string folder, Func<string, string>? expand = null)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        expand ??= Environment.ExpandEnvironmentVariables;
        var raw = Normalize(folder);
        var expanded = Normalize(expand(folder));
        foreach (var segment in path.Split(';'))
        {
            var candidate = Normalize(segment);
            if (candidate.Length == 0)
            {
                continue;
            }
            if (string.Equals(candidate, raw, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Normalize(expand(candidate)), expanded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The existing text unchanged plus <c>;folder</c> (no doubled separator).</summary>
    public static string Append(string? path, string folder)
    {
        if (string.IsNullOrEmpty(path))
        {
            return folder;
        }
        return path.EndsWith(';') ? path + folder : path + ";" + folder;
    }

    public static bool IsPresent(IUserRegistry registry, string folder, Func<string, string>? expand = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Contains(registry.Read(SubKey, ValueName)?.Data, folder, expand);
    }

    /// <exception cref="ArgumentException">Folder empty, relative, or containing <c>;</c> / <c>"</c>.</exception>
    /// <exception cref="InvalidDataException">The PATH value is not a string value; nothing is written.</exception>
    public static PathAddOutcome AddFolder(IUserRegistry registry, string folder, Func<string, string>? expand = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Validate(folder);
        var current = registry.Read(SubKey, ValueName);
        if (current is not null && current.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            throw new InvalidDataException($"PATH user bertipe {current.Kind}, bukan teks: tidak diubah");
        }
        if (Contains(current?.Data, folder, expand))
        {
            return PathAddOutcome.AlreadyPresent;
        }
        registry.Write(SubKey, ValueName, Append(current?.Data, folder), current?.Kind ?? RegistryValueKind.ExpandString);
        return PathAddOutcome.Added;
    }

    private static void Validate(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.Contains(';') || folder.Contains('"'))
        {
            throw new ArgumentException($"Folder tidak bisa dimasukkan ke PATH: \"{folder}\"", nameof(folder));
        }
    }

    internal static string Normalize(string segment)
    {
        var s = segment.Trim().Trim('"').Trim();
        return s.TrimEnd('\\', '/');
    }
}
