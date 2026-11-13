using System.Collections.Concurrent;

namespace FlutterBar.Core.Processes;

public interface IPathResolver
{
    string? Find(string command);

    void ClearCache();
}

/// <summary>Looks a command up on PATH using PATHEXT; results (including misses) are cached until <see cref="ClearCache"/>.</summary>
public sealed class PathResolver : IPathResolver
{
    private static readonly string[] DefaultExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    private readonly Func<string, string?> _getEnv;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PathResolver()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    public PathResolver(Func<string, string?> getEnv)
    {
        _getEnv = getEnv;
    }

    public string? Find(string command) => _cache.GetOrAdd(command, Search);

    public void ClearCache() => _cache.Clear();

    private string? Search(string command)
    {
        var extensions = (_getEnv("PATHEXT") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (extensions.Length == 0)
        {
            extensions = DefaultExtensions;
        }

        var dirs = (_getEnv("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in dirs)
        {
            var baseName = Path.Combine(dir.Trim('"'), command);
            foreach (var ext in extensions)
            {
                var candidate = baseName + ext;
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}
