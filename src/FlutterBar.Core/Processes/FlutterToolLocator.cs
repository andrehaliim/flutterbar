namespace FlutterBar.Core.Processes;

public sealed record ToolResolution(ProcessSpec? Spec, string? Error);

/// <summary>Decides how to invoke flutter for a project: <c>fvm flutter</c> when the project uses fvm, else <c>flutter</c> from PATH.</summary>
public sealed class FlutterToolLocator(IPathResolver paths, Func<string, bool> fileExists)
{
    public ToolResolution Resolve(string projectRoot, IReadOnlyList<string> flutterArgs)
    {
        var usesFvm = fileExists(Path.Combine(projectRoot, ".fvmrc"))
            || fileExists(Path.Combine(projectRoot, ".fvm", "fvm_config.json"));

        if (usesFvm)
        {
            var fvm = paths.Find("fvm");
            if (fvm is null)
            {
                return new ToolResolution(null, "Project memakai fvm (.fvmrc) tetapi fvm tidak ditemukan di PATH");
            }
            return new ToolResolution(new ProcessSpec(fvm, ["flutter", .. flutterArgs], projectRoot), null);
        }

        var flutter = paths.Find("flutter");
        if (flutter is null)
        {
            return new ToolResolution(null, "flutter tidak ditemukan di PATH");
        }

        if (ProcessLauncher.IsBatchFile(flutter))
        {
            return new ToolResolution(
                new ProcessSpec(ProcessLauncher.CmdExe, ["/d", "/s", "/c", flutter, .. flutterArgs], projectRoot),
                null);
        }
        return new ToolResolution(new ProcessSpec(flutter, flutterArgs, projectRoot), null);
    }
}
