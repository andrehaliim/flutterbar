namespace FlutterBar.Core.Processes;

public sealed record ToolResolution(ProcessSpec? Spec, string? Error);

/// <summary>
/// Decides how to invoke flutter (or dart) for a project: <c>fvm flutter</c> / <c>fvm dart</c> when the project uses fvm,
/// else <c>flutter</c> / <c>dart</c> from PATH.
/// </summary>
public sealed class FlutterToolLocator(IPathResolver paths, Func<string, bool> fileExists)
{
    public ToolResolution Resolve(string projectRoot, IReadOnlyList<string> flutterArgs) => ResolveTool(projectRoot, "flutter", flutterArgs);

    /// <summary>The project's <c>dart</c> (the one of its Flutter SDK under fvm), e.g. for <c>dart run build_runner</c>.</summary>
    public ToolResolution ResolveDart(string projectRoot, IReadOnlyList<string> dartArgs) => ResolveTool(projectRoot, "dart", dartArgs);

    private ToolResolution ResolveTool(string projectRoot, string tool, IReadOnlyList<string> flutterArgs)
    {
        // No escaping survives both cmd.exe and the Dart argv parser behind flutter.bat's %*, so fail closed.
        foreach (var arg in flutterArgs)
        {
            if (arg.Contains('"'))
            {
                return new ToolResolution(
                    null,
                    $"Arguments with quotes (\") aren't supported: {arg}. Use --dart-define-from-file for complex values.");
            }
        }

        string? fvmMarker = null;
        if (fileExists(Path.Combine(projectRoot, ".fvmrc")))
        {
            fvmMarker = ".fvmrc";
        }
        else if (fileExists(Path.Combine(projectRoot, ".fvm", "fvm_config.json")))
        {
            fvmMarker = ".fvm/fvm_config.json";
        }

        if (fvmMarker is not null)
        {
            var fvm = paths.Find("fvm");
            if (fvm is null)
            {
                return new ToolResolution(null, $"Project uses fvm ({fvmMarker}) but fvm wasn't found on PATH");
            }
            return new ToolResolution(new ProcessSpec(fvm, [tool, .. flutterArgs], projectRoot), null);
        }

        var flutter = paths.Find(tool);
        if (flutter is null)
        {
            return new ToolResolution(null, $"{tool} wasn't found on PATH");
        }

        if (ProcessLauncher.IsBatchFile(flutter))
        {
            return new ToolResolution(
                new ProcessSpec(ProcessLauncher.CmdExe, [.. CmdEscaper.CmdSwitches, flutter, .. flutterArgs], projectRoot),
                null);
        }
        return new ToolResolution(new ProcessSpec(flutter, flutterArgs, projectRoot), null);
    }
}
