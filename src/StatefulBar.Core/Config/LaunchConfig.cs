namespace FlutterBar.Core.Config;

public enum RunMode
{
    Debug,
    Profile,
    Release,
}

public sealed record LaunchConfig(
    string Name,
    string Program,
    string? Flavor,
    RunMode Mode,
    IReadOnlyList<string> Args);

/// <summary>A problem found while loading launch.json. <paramref name="Line"/> is 1-based.</summary>
public sealed record ConfigError(string Message, string? Field, long? Line);

public sealed record LaunchConfigLoadResult(
    bool FileExists,
    IReadOnlyList<LaunchConfig> Configs,
    IReadOnlyList<ConfigError> Errors);
