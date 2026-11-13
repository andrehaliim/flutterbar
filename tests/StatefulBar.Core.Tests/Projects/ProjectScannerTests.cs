using StatefulBar.Core.Projects;

namespace StatefulBar.Core.Tests.Projects;

public sealed class ProjectScannerTests : IDisposable
{
    private const string FlutterPubspec = "name: app\ndependencies:\n  flutter:\n    sdk: flutter\n";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("fb-scan-");

    public void Dispose() => _root.Delete(recursive: true);

    private string MakeProject(params string[] parts)
    {
        var dir = Path.Combine([_root.FullName, .. parts]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pubspec.yaml"), FlutterPubspec);
        return dir;
    }

    [Fact]
    public void Scan_FindsUpToDepth3_NotDepth4()
    {
        var d1 = MakeProject("a");
        var d3 = MakeProject("x", "y", "c");
        MakeProject("x", "y", "z", "d");

        var found = ProjectScanner.Scan([_root.FullName]);

        Assert.Equal(2, found.Count);
        Assert.Contains(d1, found);
        Assert.Contains(d3, found);
    }

    [Fact]
    public void Scan_DoesNotDescendIntoFoundProject()
    {
        var outer = MakeProject("outer");
        MakeProject("outer", "example");

        Assert.Equal([outer], ProjectScanner.Scan([_root.FullName]));
    }

    [Fact]
    public void Scan_SkipsPureDartPackages()
    {
        var dir = Path.Combine(_root.FullName, "pkg");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pubspec.yaml"), "dependencies:\n  path: ^1.9.0\n");

        Assert.Empty(ProjectScanner.Scan([_root.FullName]));
    }

    [Fact]
    public void Scan_SkipsExcludedFolders()
    {
        var ok = MakeProject("ok");
        foreach (var name in new[] { ".git", ".dart_tool", "build", "node_modules", "android", "ios", "windows", "linux", "macos", "web", ".hidden" })
        {
            MakeProject(name, "inner");
        }

        Assert.Equal([ok], ProjectScanner.Scan([_root.FullName]));
    }

    [Fact]
    public void Scan_UnreadableFolder_IsSkippedAndReported_OthersStillFound()
    {
        // IOException on one folder (here a locked pubspec; also offline OneDrive placeholders, dead junctions) must
        // not wipe the whole list.
        var locked = MakeProject("a-locked");
        var ok = MakeProject("b-ok");
        var skipped = new List<(string Path, Exception Error)>();

        using (new FileStream(Path.Combine(locked, "pubspec.yaml"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal([ok], ProjectScanner.Scan([_root.FullName], onSkipped: (path, ex) => skipped.Add((path, ex))));
        }

        var (path, error) = Assert.Single(skipped);
        Assert.Equal(locked, path);
        Assert.IsAssignableFrom<IOException>(error);
    }

    [Fact]
    public void Scan_PathWithSpacesAndUnicode()
    {
        var dir = MakeProject("My Projects", "aplikasi-ü");

        Assert.Equal([dir], ProjectScanner.Scan([_root.FullName]));
    }

    [Fact]
    public void Scan_MissingRoot_IsIgnored()
    {
        var ok = MakeProject("ok");

        var found = ProjectScanner.Scan([Path.Combine(_root.FullName, "nope"), _root.FullName]);

        Assert.Equal([ok], found);
    }

    [Fact]
    public void Scan_OverlappingRoots_Deduplicated()
    {
        var ok = MakeProject("ok");

        var found = ProjectScanner.Scan([_root.FullName, _root.FullName.ToUpperInvariant() + "\\"]);

        Assert.Single(found);
        Assert.Equal(ok, found[0], ignoreCase: true);
    }
}
