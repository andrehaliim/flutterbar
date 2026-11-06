using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Tests.Processes;

public class FlutterToolLocatorTests
{
    private const string Root = @"C:\proj";

    private sealed class FakePaths(Dictionary<string, string> map) : IPathResolver
    {
        public string? Find(string command) => map.GetValueOrDefault(command);

        public void ClearCache()
        {
        }
    }

    [Fact]
    public void Fvmrc_UsesFvmExe()
    {
        var locator = new FlutterToolLocator(
            new FakePaths(new() { ["fvm"] = @"C:\x\fvm.exe", ["flutter"] = @"C:\f\flutter.bat" }),
            p => p == Path.Combine(Root, ".fvmrc"));

        var r = locator.Resolve(Root, ["run", "--machine"]);

        Assert.Null(r.Error);
        Assert.Equal(@"C:\x\fvm.exe", r.Spec!.FileName);
        Assert.Equal(["flutter", "run", "--machine"], r.Spec.Args);
        Assert.Equal(Root, r.Spec.WorkingDirectory);
    }

    [Fact]
    public void FvmConfigJson_AlsoCountsAsFvm()
    {
        var locator = new FlutterToolLocator(
            new FakePaths(new() { ["fvm"] = @"C:\x\fvm.exe" }),
            p => p == Path.Combine(Root, ".fvm", "fvm_config.json"));

        var r = locator.Resolve(Root, ["devices"]);

        Assert.Equal(@"C:\x\fvm.exe", r.Spec!.FileName);
        Assert.Equal("flutter", r.Spec.Args[0]);
    }

    [Fact]
    public void Fvmrc_FvmMissing_ErrorNoFallback()
    {
        var locator = new FlutterToolLocator(
            new FakePaths(new() { ["flutter"] = @"C:\f\flutter.bat" }),
            p => p == Path.Combine(Root, ".fvmrc"));

        var r = locator.Resolve(Root, ["devices"]);

        Assert.Null(r.Spec);
        Assert.Contains("fvm", r.Error);
    }

    [Fact]
    public void NoFvm_FlutterBat_WrappedInCmd()
    {
        var locator = new FlutterToolLocator(new FakePaths(new() { ["flutter"] = @"C:\f\flutter.bat" }), _ => false);

        var r = locator.Resolve(Root, ["devices", "--machine"]);

        Assert.Null(r.Error);
        Assert.Equal("cmd.exe", r.Spec!.FileName);
        Assert.Equal(["/d", "/s", "/c", @"C:\f\flutter.bat", "devices", "--machine"], r.Spec.Args);
    }

    [Fact]
    public void NoFvm_FlutterExe_RunsDirectly()
    {
        var locator = new FlutterToolLocator(new FakePaths(new() { ["flutter"] = @"C:\f\flutter.exe" }), _ => false);

        var r = locator.Resolve(Root, ["doctor"]);

        Assert.Equal(@"C:\f\flutter.exe", r.Spec!.FileName);
        Assert.Equal(["doctor"], r.Spec.Args);
    }

    [Fact]
    public void NoFlutter_Error()
    {
        var locator = new FlutterToolLocator(new FakePaths([]), _ => false);

        var r = locator.Resolve(Root, ["devices"]);

        Assert.Null(r.Spec);
        Assert.Contains("flutter", r.Error);
    }
}
