using StatefulBar.Core.Config;

namespace StatefulBar.Core.Tests.Config;

public sealed class IdeConfigImporterTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("flutterbar-test-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Root => _root.FullName;

    private void Put(string fixtureRelative, string projectRelative)
    {
        var dest = Path.Combine(Root, projectRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(TestPaths.Fixture(fixtureRelative.Split('/')), dest);
    }

    private void PutAll()
    {
        Put("idea/workspace.xml", ".idea/workspace.xml");
        Put("idea/run/dev.run.xml", ".run/dev.run.xml");
        Put("vscode/launch.json", ".vscode/launch.json");
    }

    private IReadOnlyList<ImportCandidate> Discover(params LaunchConfig[] existing) =>
        IdeConfigImporter.Discover(Root, existing);

    private static LaunchConfig Cfg(string name) => new(name, "lib/main.dart", null, RunMode.Debug, []);

    [Fact]
    public void Discover_Idea_MapsBetaConfig()
    {
        PutAll();
        var beta = Assert.Single(Discover(), c => c.Source == "idea" && c.Config.Name == "beta");
        Assert.Equal("lib/main.dart", beta.Config.Program);
        Assert.Equal("beta", beta.Config.Flavor);
        Assert.Equal(RunMode.Debug, beta.Config.Mode);
        Assert.Contains("--dart-define=APP_NAME=GTS Mobile Beta", beta.Config.Args);
        Assert.Contains("--dart-define=FLAVOR=beta", beta.Config.Args);
        Assert.False(beta.AlreadyExists);
    }

    [Fact]
    public void Discover_RunFolder_IsIncluded()
    {
        PutAll();
        var dev = Assert.Single(Discover(), c => c.Source == "idea" && c.Config.Name == "dev");
        Assert.Equal("dev", dev.Config.Flavor);
        Assert.Equal("lib/main.dart", dev.Config.Program);
    }

    [Fact]
    public void Discover_Idea_FlavorArgInAdditionalArgs_BuildFlavorWins()
    {
        var path = Path.Combine(Root, ".run", "x.run.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            <component name="ProjectRunConfigurationManager">
              <configuration name="x" type="FlutterRunConfigurationType">
                <option name="additionalArgs" value="--flavor other --keep" />
                <option name="buildFlavor" value="real" />
              </configuration>
            </component>
            """);
        var x = Assert.Single(Discover());
        Assert.Equal("real", x.Config.Flavor);
        Assert.Equal(["--keep"], x.Config.Args);
        Assert.Equal("lib/main.dart", x.Config.Program);
    }

    [Fact]
    public void Discover_Vscode_LiftsFlavor()
    {
        PutAll();
        var dev = Assert.Single(Discover(), c => c.Source == "vscode" && c.Config.Name == "GTS Mobile Dev");
        Assert.Equal("dev", dev.Config.Flavor);
        Assert.Equal("lib/main.dart", dev.Config.Program);
        Assert.Equal(RunMode.Debug, dev.Config.Mode);
        Assert.DoesNotContain("--flavor", dev.Config.Args);
        Assert.DoesNotContain("dev", dev.Config.Args);
        Assert.Contains("--dart-define=APP_NAME=GTS Mobile Dev", dev.Config.Args);
        Assert.Equal(3, dev.Config.Args.Count);
    }

    [Fact]
    public void Discover_Vscode_FlavorEqualsForm()
    {
        PutAll();
        var live = Assert.Single(Discover(), c => c.Config.Name == "GTS Mobile Live Profile");
        Assert.Equal("live", live.Config.Flavor);
        Assert.Equal(RunMode.Profile, live.Config.Mode);
        Assert.Equal("lib/main_live.dart", live.Config.Program);
        Assert.Equal(["--dart-define=mode=profile"], live.Config.Args);
    }

    [Fact]
    public void Discover_MarksAlreadyExisting()
    {
        PutAll();
        var result = Discover(Cfg("beta"));
        Assert.True(Assert.Single(result, c => c.Source == "idea" && c.Config.Name == "beta").AlreadyExists);
        Assert.False(Assert.Single(result, c => c.Config.Name == "live").AlreadyExists);
    }

    [Fact]
    public void Discover_IgnoresNonDartVscodeConfigs()
    {
        PutAll();
        Assert.DoesNotContain(Discover(), c => c.Config.Name == "Node Helper");
    }

    [Fact]
    public void Discover_NoIdeFiles_ReturnsEmpty() => Assert.Empty(Discover());

    [Fact]
    public void Discover_MalformedVscode_ThrowsWithPath()
    {
        var path = Path.Combine(Root, ".vscode", "launch.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"configurations\": [ ");
        var ex = Assert.Throws<InvalidDataException>(() => Discover());
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Discover_MalformedIdea_ThrowsWithPath()
    {
        var path = Path.Combine(Root, ".idea", "workspace.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<project><oops>");
        var ex = Assert.Throws<InvalidDataException>(() => Discover());
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Merge_SuffixesCollidingNames()
    {
        var idea = new ImportCandidate(Cfg("beta"), "idea", false);
        var vscode = new ImportCandidate(Cfg("beta"), "vscode", false);
        var merged = IdeConfigImporter.Merge([], [idea, vscode]);
        Assert.Equal(["beta", "beta (vscode)"], merged.Select(c => c.Name));
    }

    [Fact]
    public void Merge_SkipsNamesAlreadyInExisting()
    {
        var existing = new[] { Cfg("beta") };
        var selected = new[]
        {
            new ImportCandidate(Cfg("beta"), "idea", true),
            new ImportCandidate(Cfg("live"), "idea", false),
        };
        var merged = IdeConfigImporter.Merge(existing, selected);
        Assert.Equal(["beta", "live"], merged.Select(c => c.Name));
        Assert.Same(existing[0], merged[0]);
    }
}
