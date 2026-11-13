using FlutterBar.Core.Projects;
using FlutterBar.Core.Settings;

namespace FlutterBar.Core.Tests.Projects;

public sealed class ProjectListEditorTests
{
    private readonly AppSettings _settings = new();

    [Fact]
    public void AddScanRoot_StoresNormalized_AndDeduplicates()
    {
        Assert.True(ProjectListEditor.AddScanRoot(_settings, @"C:\Work\"));
        Assert.False(ProjectListEditor.AddScanRoot(_settings, @"c:\work"));
        Assert.False(ProjectListEditor.AddScanRoot(_settings, @"C:\Work\.\"));

        Assert.Equal([@"C:\Work"], _settings.ScanRoots);
    }

    [Fact]
    public void RemoveScanRoot_RemovesEveryVariant()
    {
        _settings.ScanRoots = [@"C:\Work", @"c:\work\", @"D:\Other"];

        Assert.True(ProjectListEditor.RemoveScanRoot(_settings, @"C:\WORK"));
        Assert.False(ProjectListEditor.RemoveScanRoot(_settings, @"C:\Work"));

        Assert.Equal([@"D:\Other"], _settings.ScanRoots);
    }

    [Fact]
    public void ManualProjects_AddAndRemove()
    {
        _settings.ManualProjects = [@"D:\apps\one\"];

        Assert.False(ProjectListEditor.AddManual(_settings, @"d:\APPS\one"));
        Assert.True(ProjectListEditor.AddManual(_settings, @"D:\apps\two"));
        Assert.True(ProjectListEditor.RemoveManual(_settings, @"D:\apps\one"));

        Assert.Equal([@"D:\apps\two"], _settings.ManualProjects);
    }

    [Fact]
    public void HideAndUnhide()
    {
        Assert.True(ProjectListEditor.Hide(_settings, @"C:\Work\gts-mobile\"));
        Assert.False(ProjectListEditor.Hide(_settings, @"c:\work\GTS-MOBILE"));
        Assert.Equal([@"C:\Work\gts-mobile"], _settings.HiddenProjects);

        Assert.True(ProjectListEditor.Unhide(_settings, @"C:\WORK\gts-mobile"));
        Assert.Empty(_settings.HiddenProjects);
        Assert.False(ProjectListEditor.Unhide(_settings, @"C:\Work\gts-mobile"));
    }

    [Fact]
    public void Hide_RemovesProjectFromCatalog()
    {
        _settings.ManualProjects = [@"D:\apps\one"];
        ProjectListEditor.Hide(_settings, @"D:\apps\one");

        Assert.Empty(ProjectCatalog.Build([], _settings, _ => true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\dir")]
    public void InvalidPath_Throws(string path) => Assert.Throws<ArgumentException>(() => ProjectListEditor.AddScanRoot(_settings, path));

    [Fact]
    public void Remove_HandWrittenBlankOrRelativeEntry_ByExactString()
    {
        _settings.ScanRoots = ["", "   ", @"relative\dir", @"C:\Work"];
        _settings.HiddenProjects = [@"old\project"];

        Assert.True(ProjectListEditor.RemoveScanRoot(_settings, ""));
        Assert.True(ProjectListEditor.RemoveScanRoot(_settings, "   "));
        Assert.True(ProjectListEditor.RemoveScanRoot(_settings, @"relative\dir"));
        Assert.True(ProjectListEditor.Unhide(_settings, @"old\project"));

        Assert.Equal([@"C:\Work"], _settings.ScanRoots);
        Assert.Empty(_settings.HiddenProjects);
    }

    [Fact]
    public void Remove_UnknownRelativePath_ReturnsFalseWithoutThrowing()
    {
        _settings.ManualProjects = [@"D:\apps\one"];

        Assert.False(ProjectListEditor.RemoveManual(_settings, @"not\stored"));
        Assert.Equal([@"D:\apps\one"], _settings.ManualProjects);
    }
}
