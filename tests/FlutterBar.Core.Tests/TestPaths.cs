namespace FlutterBar.Core.Tests;

public static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string Fixture(params string[] parts) =>
        Path.Combine([RepoRoot, "tests", "fixtures", .. parts]);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FlutterBar.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("FlutterBar.sln not found above " + AppContext.BaseDirectory);
    }
}
