using FlutterBar.Core.Projects;
using FlutterBar.Core.Settings;

namespace FlutterBar.Core.Tests.Projects;

public class ProjectCatalogTests
{
    private static bool AllExist(string _) => true;

    [Fact]
    public void Build_MergesManual_RemovesHidden()
    {
        var settings = new AppSettings();
        settings.ManualProjects.Add("D:\\Other\\manual");
        settings.ManualProjects.Add("c:\\work\\a\\"); // duplicate of scanned: different case + trailing slash
        settings.HiddenProjects.Add("C:\\WORK\\hidden");

        var result = ProjectCatalog.Build(["C:\\Work\\a", "C:\\Work\\hidden", "C:\\Work\\b"], settings, AllExist);

        Assert.Equal(["a", "b", "manual"], result.Select(p => p.DisplayName));
    }

    [Fact]
    public void Build_OrdersByLastUsedDescThenName()
    {
        var settings = new AppSettings();
        settings.Projects["c:\\work\\old"] = new ProjectState { LastUsed = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        settings.Projects["C:\\Work\\new"] = new ProjectState { LastUsed = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) };

        var result = ProjectCatalog.Build(
            ["C:\\Work\\zeta", "C:\\Work\\alpha", "C:\\Work\\old", "C:\\Work\\new"], settings, AllExist);

        Assert.Equal(["new", "old", "alpha", "zeta"], result.Select(p => p.DisplayName));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), result[0].LastUsed);
        Assert.Null(result[2].LastUsed);
    }

    [Fact]
    public void Build_DuplicateFolderNames_AppendsParent()
    {
        var result = ProjectCatalog.Build(
            ["C:\\Work\\GTSmobile\\gts-mobile", "C:\\Work\\other\\gts-mobile", "C:\\Work\\solo"],
            new AppSettings(), AllExist);

        var names = result.Select(p => p.DisplayName).ToList();
        Assert.Contains("gts-mobile (GTSmobile)", names);
        Assert.Contains("gts-mobile (other)", names);
        Assert.Contains("solo", names);
    }

    [Fact]
    public void Build_BlankEntries_AreIgnored()
    {
        var settings = new AppSettings();
        settings.ManualProjects.AddRange(["", "  "]);
        settings.HiddenProjects.Add("");

        var result = ProjectCatalog.Build(["C:\\Work\\a", ""], settings, AllExist);

        Assert.Equal(["a"], result.Select(p => p.DisplayName));
    }

    [Fact]
    public void Build_MissingDirectory_MarkedUnavailable()
    {
        var result = ProjectCatalog.Build(
            ["C:\\Work\\here", "C:\\Work\\gone"], new AppSettings(), p => p.EndsWith("here", StringComparison.Ordinal));

        Assert.True(result.Single(p => p.DisplayName == "here").Available);
        Assert.False(result.Single(p => p.DisplayName == "gone").Available);
    }
}
