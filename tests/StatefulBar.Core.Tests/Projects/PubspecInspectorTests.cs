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

    [Fact]
    public void BuildRunner_InDevDependencies_True() =>
        Assert.True(PubspecInspector.UsesBuildRunner(
            "dependencies:\r\n  flutter:\r\n    sdk: flutter\r\ndev_dependencies:\r\n  build_runner: ^2.4.9 # codegen\r\n"));

    [Fact]
    public void BuildRunner_InDependencies_True() =>
        Assert.True(PubspecInspector.UsesBuildRunner("dependencies:\n  build_runner:\n    version: ^2.4.0\n"));

    [Fact]
    public void BuildRunner_Absent_False() =>
        Assert.False(PubspecInspector.UsesBuildRunner("dependencies:\n  path: ^1.9.0\ndev_dependencies:\n  build_runner_core: ^7.0.0\n"));

    [Fact]
    public void BuildRunner_CommentedNestedOrTopLevel_False() =>
        Assert.False(PubspecInspector.UsesBuildRunner(
            "dev_dependencies:\n  # build_runner: ^2.4.0\n  my_pkg:\n    build_runner: x\nbuild_runner: ^1.0.0\n"));
}
