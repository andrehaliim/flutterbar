using FlutterBar.Core.Projects;

namespace FlutterBar.Core.Tests.Projects;

public class PubspecInspectorTests
{
    [Fact]
    public void FlutterApp_True() =>
        Assert.True(PubspecInspector.IsFlutterApp("name: x\ndependencies:\n  flutter:\n    sdk: flutter\n"));

    [Fact]
    public void FlutterApp_CrLfAndComments_True() =>
        Assert.True(PubspecInspector.IsFlutterApp("dependencies: # deps\r\n  # c\r\n  flutter:\r\n    sdk: flutter # x\r\n  path: ^1.0.0\r\n"));

    [Fact]
    public void PureDartPackage_False() =>
        Assert.False(PubspecInspector.IsFlutterApp("dependencies:\n  path: ^1.9.0\n"));

    [Fact]
    public void DevDependencyOnlyFlutterTest_False() =>
        Assert.False(PubspecInspector.IsFlutterApp(
            "dependencies:\n  path: ^1.9.0\ndev_dependencies:\n  flutter_test:\n    sdk: flutter\n"));

    [Fact]
    public void FlutterOnlyUnderDevDependencies_False() =>
        Assert.False(PubspecInspector.IsFlutterApp(
            "dependencies:\n  path: ^1.9.0\ndev_dependencies:\n  flutter:\n    sdk: flutter\n"));
}
