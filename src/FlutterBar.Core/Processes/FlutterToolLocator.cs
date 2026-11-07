namespace FlutterBar.Core.Processes;

public sealed record ToolResolution(ProcessSpec? Spec, string? Error);

/// <summary>Decides how to invoke flutter for a project: <c>fvm flutter</c> when the project uses fvm, else <c>flutter</c> from PATH.</summary>
public sealed class FlutterToolLocator(IPathResolver paths, Func<string, bool> fileExists)
{
    public ToolResolution Resolve(string projectRoot, IReadOnlyList<string> flutterArgs)
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
            return new ToolResolution(new ProcessSpec(fvm, ["flutter", .. flutterArgs], projectRoot), null);
        }

        var flutter = paths.Find("flutter");
        if (flutter is null)
        {
            return new ToolResolution(null, "flutter wasn't found on PATH");
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
