using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Tests.Processes;

public class PathResolverTests
{
    [Fact]
    public void Find_UsesPathExt_AndCaches_UntilClearCache()
    {
        var dir = Directory.CreateTempSubdirectory("flutterbar-path-");
        try
        {
            var env = new Dictionary<string, string?>
            {
                ["PATH"] = @"C:\nonexistent-dir;" + dir.FullName,
                ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            };
            var resolver = new PathResolver(k => env.GetValueOrDefault(k));
            Assert.Null(resolver.Find("mytool"));

            var bat = Path.Combine(dir.FullName, "mytool.bat");
            File.WriteAllText(bat, "@echo off");

            Assert.Null(resolver.Find("mytool")); // cached miss
            resolver.ClearCache();
            Assert.Equal(bat, resolver.Find("mytool"), ignoreCase: true);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Find_PrefersEarlierPathExtEntry()
    {
        var dir = Directory.CreateTempSubdirectory("flutterbar-path-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "t.cmd"), "");
            File.WriteAllText(Path.Combine(dir.FullName, "t.exe"), "");
            var env = new Dictionary<string, string?> { ["PATH"] = dir.FullName, ["PATHEXT"] = ".EXE;.CMD" };

            var found = new PathResolver(k => env.GetValueOrDefault(k)).Find("t");

            Assert.Equal(Path.Combine(dir.FullName, "t.exe"), found, ignoreCase: true);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
