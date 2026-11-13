using FlutterBar.Core.Config;

namespace FlutterBar.Core.Tests.Config;

public sealed class LaunchConfigStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("flutterbar-test-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Root => _root.FullName;

    private void MakeProgram(string relative = "lib/main.dart")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "void main() {}");
    }

    private void WriteLaunch(string json)
    {
        var path = LaunchConfigStore.PathFor(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void PathFor_PointsToFlutterbarFolder() =>
        Assert.Equal(Path.Combine(Root, ".flutterbar", "launch.json"), LaunchConfigStore.PathFor(Root));

    [Fact]
    public void Load_MissingFile_ReturnsFileExistsFalse()
    {
        var result = LaunchConfigStore.Load(Root);

        Assert.False(result.FileExists);
        Assert.Empty(result.Configs);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Load_Jsonc_WithCommentsAndTrailingCommas()
    {
        MakeProgram();
        WriteLaunch(File.ReadAllText(TestPaths.Fixture("launch", "gts-beta.jsonc")));

        var result = LaunchConfigStore.Load(Root);

        Assert.Empty(result.Errors);
        var config = Assert.Single(result.Configs);
        Assert.Equal("beta", config.Name);
        Assert.Equal("lib/main.dart", config.Program);
        Assert.Equal("beta", config.Flavor);
        Assert.Equal(RunMode.Debug, config.Mode);
        Assert.Equal("--dart-define=APP_NAME=GTS Mobile Beta", config.Args[2]);
    }

    [Fact]
    public void Load_DefaultsProgramAndMode()
    {
        MakeProgram();
        WriteLaunch("""{"version":1,"configurations":[{"name":"x"}]}""");

        var result = LaunchConfigStore.Load(Root);

        var config = Assert.Single(result.Configs);
        Assert.Equal("lib/main.dart", config.Program);
        Assert.Equal(RunMode.Debug, config.Mode);
        Assert.Null(config.Flavor);
        Assert.Empty(config.Args);
    }

    [Fact]
    public void Load_InvalidJson_ReportsLine()
    {
        WriteLaunch("{\n  \"version\": 1,\n  \"configurations\": [\n    { \"name\" \"x\" }\n  ]\n}");

        var result = LaunchConfigStore.Load(Root);

        Assert.Empty(result.Configs);
        var error = Assert.Single(result.Errors);
        Assert.Equal(4, error.Line);
    }

    [Theory]
    [InlineData("""{"version":2,"configurations":[{"name":"x"}]}""", "version")]
    [InlineData("""{"version":1,"configurations":[]}""", "configurations")]
    [InlineData("""{"version":1,"configurations":[{"name":"a"},{"name":"a"}]}""", "configurations[1].name")]
    [InlineData("""{"version":1,"configurations":[{"name":"a","mode":"fast"}]}""", "configurations[0].mode")]
    [InlineData("""{"version":1,"configurations":[{"name":"a","program":"lib/missing.dart"}]}""", "configurations[0].program")]
    [InlineData("""{"version":1,"configurations":[{}]}""", "configurations[0].name")]
    public void Load_ValidationErrors(string json, string field)
    {
        MakeProgram();
        WriteLaunch(json);

        var result = LaunchConfigStore.Load(Root);

        Assert.True(result.FileExists);
        Assert.Empty(result.Configs);
        Assert.Contains(result.Errors, e => e.Field == field);
    }

    [Fact]
    public void Load_RereadsAfterExternalEdit()
    {
        MakeProgram();
        WriteLaunch("""{"version":1,"configurations":[{"name":"old"}]}""");
        Assert.Equal("old", Assert.Single(LaunchConfigStore.Load(Root).Configs).Name);

        WriteLaunch("""{"version":1,"configurations":[{"name":"new"}]}""");

        Assert.Equal("new", Assert.Single(LaunchConfigStore.Load(Root).Configs).Name);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        MakeProgram();
        MakeProgram("lib/main_dev.dart");
        var configs = new[]
        {
            new LaunchConfig("beta", "lib/main.dart", "beta", RunMode.Profile,
                ["--dart-define=APP_NAME=GTS \"Mobile\" Beta", "-v"]),
            new LaunchConfig("dev", "lib/main_dev.dart", null, RunMode.Release, []),
        };

        LaunchConfigStore.Save(Root, configs);
        var result = LaunchConfigStore.Load(Root);

        Assert.True(result.FileExists);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Configs.Count);
        for (var i = 0; i < configs.Length; i++)
        {
            Assert.Equal(configs[i].Name, result.Configs[i].Name);
            Assert.Equal(configs[i].Program, result.Configs[i].Program);
            Assert.Equal(configs[i].Flavor, result.Configs[i].Flavor);
            Assert.Equal(configs[i].Mode, result.Configs[i].Mode);
            Assert.Equal(configs[i].Args, result.Configs[i].Args);
        }
    }

    [Fact]
    public void Save_IsAtomic_NoTmpFileLeft()
    {
        LaunchConfigStore.Save(Root, [LaunchConfigStore.CreateDefault()]);
        LaunchConfigStore.Save(Root, [LaunchConfigStore.CreateDefault()]);

        var dir = Path.GetDirectoryName(LaunchConfigStore.PathFor(Root))!;
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.True(File.Exists(LaunchConfigStore.PathFor(Root)));
    }

    [Fact]
    public void CreateDefault_HasExpectedValues()
    {
        var config = LaunchConfigStore.CreateDefault();

        Assert.Equal("default", config.Name);
        Assert.Equal("lib/main.dart", config.Program);
        Assert.Null(config.Flavor);
        Assert.Equal(RunMode.Debug, config.Mode);
        Assert.Empty(config.Args);
    }
}
