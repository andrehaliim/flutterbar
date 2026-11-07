using FlutterBar.Core.Config;

namespace FlutterBar.Core.Tests.Config;

public sealed class ImportPlanTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("flutterbar-import-");

    public ImportPlanTests()
    {
        // launch.json validation needs the program file to exist.
        Directory.CreateDirectory(Path.Combine(Root, "lib"));
        File.WriteAllText(Path.Combine(Root, "lib", "main.dart"), "void main() {}");
    }

    public void Dispose() => _root.Delete(recursive: true);

    private string Root => _root.FullName;

    private void Put(string fixtureRelative, string projectRelative)
    {
        var dest = Path.Combine(Root, projectRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(TestPaths.Fixture(fixtureRelative.Split('/')), dest);
    }

    private void PutIdeFiles()
    {
        Put("idea/workspace.xml", ".idea/workspace.xml");
        Put("vscode/launch.json", ".vscode/launch.json");
    }

    private static LaunchConfig Cfg(string name) => new(name, "lib/main.dart", null, RunMode.Debug, []);

    private static ImportCandidate Candidate(string name, string source, bool exists = false) => new(Cfg(name), source, exists);

    [Theory]
    [InlineData("idea", "Android Studio")]
    [InlineData("vscode", "VS Code")]
    public void SourceLabel_NamesTheIde(string source, string label) => Assert.Equal(label, ImportPlan.SourceLabel(source));

    [Fact]
    public void Rows_NewCandidate_CheckedAndEnabled()
    {
        var row = Assert.Single(ImportPlan.Rows([Candidate("beta", "idea")]));

        Assert.True(row.IsChecked);
        Assert.True(row.IsEnabled);
        Assert.Null(row.Note);
        Assert.Equal("beta", row.Title);
        Assert.StartsWith("Android Studio", row.Detail);
    }

    [Fact]
    public void Rows_AlreadyExists_UncheckedDisabledAndLabelled()
    {
        var row = Assert.Single(ImportPlan.Rows([Candidate("beta", "vscode", exists: true)]));

        Assert.False(row.IsChecked);
        Assert.False(row.IsEnabled);
        Assert.Equal("sudah ada", row.Note);
        Assert.StartsWith("VS Code", row.Detail);
    }

    [Fact]
    public void Rows_DetailShowsFlavorModeAndProgram()
    {
        var config = new LaunchConfig("live", "lib/main_live.dart", "live", RunMode.Profile, ["--x"]);

        var row = Assert.Single(ImportPlan.Rows([new ImportCandidate(config, "idea", false)]));

        Assert.Equal("Android Studio · flavor live · profile · lib/main_live.dart", row.Detail);
        Assert.Contains("--x", row.ToolTip);
    }

    [Fact]
    public void Discover_IdeAndVscodeFixtures_FindsAllFlutterConfigs()
    {
        PutIdeFiles();

        var discovery = ImportPlan.Discover(Root);

        Assert.Null(discovery.Error);
        Assert.False(discovery.LaunchJsonExists);
        var names = discovery.Rows.Select(r => r.Title).ToList();
        Assert.True(names.Count >= 5, string.Join(", ", names));
        Assert.Contains("beta", names);
        Assert.Contains("live", names);
        Assert.Contains("GTS Mobile Dev", names);
        Assert.Contains("GTS Mobile Beta", names);
        Assert.Contains("GTS Mobile Live", names);
        // Only lib/main.dart exists here: "GTS Mobile Live Profile" (lib/main_live.dart) is offered unchecked.
        Assert.All(discovery.Rows.Where(r => r.Candidate.Config.Program == "lib/main.dart"), r => Assert.True(r.IsChecked));
        var profile = Assert.Single(discovery.Rows, r => r.Title == "GTS Mobile Live Profile");
        Assert.False(profile.IsChecked);
        Assert.True(profile.IsEnabled);
        Assert.Equal(ImportPlan.MissingProgramNote, profile.Note);
    }

    [Fact]
    public void Rows_MissingProgram_UncheckedButSelectable()
    {
        var row = Assert.Single(ImportPlan.Rows([Candidate("beta", "idea")], _ => false));

        Assert.False(row.IsChecked);
        Assert.True(row.IsEnabled);
        Assert.Equal("file program tidak ada — config akan merah setelah diimport", row.Note);
    }

    [Fact]
    public void Rows_AlreadyExists_WinsOverMissingProgram()
    {
        var row = Assert.Single(ImportPlan.Rows([Candidate("beta", "idea", exists: true)], _ => false));

        Assert.False(row.IsEnabled);
        Assert.Equal("sudah ada", row.Note);
    }

    [Fact]
    public void Discover_ExistingNames_MarkedAlreadyExists()
    {
        PutIdeFiles();
        LaunchConfigStore.Save(Root, [Cfg("beta")]);

        var discovery = ImportPlan.Discover(Root);

        Assert.True(discovery.LaunchJsonExists);
        var beta = Assert.Single(discovery.Rows, r => r.Title == "beta");
        Assert.False(beta.IsChecked);
        Assert.Equal("sudah ada", beta.Note);
    }

    [Fact]
    public void Discover_MalformedIdeFile_ReturnsErrorInsteadOfThrowing()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".idea"));
        File.WriteAllText(Path.Combine(Root, ".idea", "workspace.xml"), "<project><broken");

        var discovery = ImportPlan.Discover(Root);

        Assert.Empty(discovery.Rows);
        Assert.NotNull(discovery.Error);
        Assert.Contains("workspace.xml", discovery.Error);
        Assert.IsType<InvalidDataException>(discovery.Exception);
    }

    [Fact]
    public void Discover_InvalidLaunchJson_ReturnsError()
    {
        PutIdeFiles();
        Directory.CreateDirectory(Path.Combine(Root, ".flutterbar"));
        File.WriteAllText(LaunchConfigStore.PathFor(Root), "{ \"version\": 1, oops }");

        var discovery = ImportPlan.Discover(Root);

        Assert.Empty(discovery.Rows);
        Assert.Contains("launch.json tidak valid", discovery.Error);
    }

    [Fact]
    public void Discover_NothingFound_SaysSo()
    {
        var discovery = ImportPlan.Discover(Root);

        Assert.Empty(discovery.Rows);
        Assert.Equal(ImportPlan.NothingFoundText, discovery.Error);
    }

    [Fact]
    public void Apply_MissingLaunchJson_CreatesItWithSelected()
    {
        var added = ImportPlan.Apply(Root, [Candidate("beta", "idea"), Candidate("live", "idea")]);

        Assert.Equal(["beta", "live"], added);
        var loaded = LaunchConfigStore.Load(Root);
        Assert.Empty(loaded.Errors);
        Assert.Equal(["beta", "live"], loaded.Configs.Select(c => c.Name));
    }

    [Fact]
    public void Apply_AppendsAfterExisting_AndSuffixesCollisions()
    {
        LaunchConfigStore.Save(Root, [Cfg("default")]);

        var added = ImportPlan.Apply(Root, [Candidate("beta", "idea"), Candidate("beta", "vscode"), Candidate("default", "vscode")]);

        Assert.Equal(["beta", "beta (vscode)"], added);
        Assert.Equal(["default", "beta", "beta (vscode)"], LaunchConfigStore.Load(Root).Configs.Select(c => c.Name));
    }

    [Fact]
    public void Apply_NothingNew_DoesNotWrite()
    {
        LaunchConfigStore.Save(Root, [Cfg("beta")]);
        var path = LaunchConfigStore.PathFor(Root);
        File.WriteAllText(path, File.ReadAllText(path) + "\n// keep me");

        var added = ImportPlan.Apply(Root, [Candidate("beta", "idea")]);

        Assert.Empty(added);
        Assert.EndsWith("// keep me", File.ReadAllText(path));
    }

    [Fact]
    public void Apply_InvalidLaunchJson_Throws_AndKeepsFile()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".flutterbar"));
        File.WriteAllText(LaunchConfigStore.PathFor(Root), "{ broken");

        Assert.Throws<InvalidDataException>(() => ImportPlan.Apply(Root, [Candidate("beta", "idea")]));

        Assert.Equal("{ broken", File.ReadAllText(LaunchConfigStore.PathFor(Root)));
    }
}
