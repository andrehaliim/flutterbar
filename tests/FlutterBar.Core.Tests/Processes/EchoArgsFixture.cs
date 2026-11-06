using System.Text.Json;
using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Tests.Processes;

/// <summary>Copies the built EchoArgs helper plus echoargs.cmd into a temp folder.</summary>
internal sealed class EchoArgsFixture : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("flutterbar-echo-");

    public EchoArgsFixture(string subDirectory = "")
    {
        Dir = subDirectory.Length == 0
            ? _root.FullName
            : Directory.CreateDirectory(Path.Combine(_root.FullName, subDirectory)).FullName;

        var source = FindEchoArgsOutput();
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(Dir, Path.GetFileName(file)), overwrite: true);
        }
        File.Copy(TestPaths.Fixture("echoargs", "echoargs.cmd"), Path.Combine(Dir, "echoargs.cmd"), overwrite: true);
    }

    public string Dir { get; }

    public string Cmd => Path.Combine(Dir, "echoargs.cmd");

    public string Exe => Path.Combine(Dir, "echoargs.exe");

    public ProcessSpec Spec(string fileName, params string[] args) => new(fileName, args, Dir);

    public static string[] ParseArgs(string stdout) =>
        JsonSerializer.Deserialize<string[]>(stdout.Trim()) ?? throw new InvalidOperationException("null JSON");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A just-killed process may still hold a handle briefly; the OS temp cleaner reaps the leftovers.
        }
    }

    private static string FindEchoArgsOutput()
    {
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name; // Debug / Release
        var dir = Path.Combine(TestPaths.RepoRoot, "tests", "EchoArgs", "bin", config, "net10.0-windows");
        if (!File.Exists(Path.Combine(dir, "echoargs.exe")))
        {
            throw new FileNotFoundException("EchoArgs output not built", dir);
        }
        return dir;
    }
}
