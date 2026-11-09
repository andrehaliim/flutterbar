using FlutterBar.Core.Docking;
using FlutterBar.Core.Layout;

namespace FlutterBar.Core.Tests.Docking;

public class TaskbarTargetsTests
{
    // The spike setup: main 1920x1080, a portrait monitor left of it (negative coordinates).
    private static readonly TaskbarCandidate Main = new(@"\\.\DISPLAY1", true, new Box(0, 0, 1920, 1080));
    private static readonly TaskbarCandidate Portrait = new(@"\\.\DISPLAY2", false, new Box(-1080, 0, 0, 1920));

    [Fact]
    public void Pick_NullOrBlank_IsTheMainTaskbar()
    {
        Assert.Equal(1, TaskbarTargets.Pick([Portrait, Main], null));
        Assert.Equal(1, TaskbarTargets.Pick([Portrait, Main], "  "));
    }

    [Fact]
    public void Pick_WantedDevice_CaseInsensitive()
    {
        Assert.Equal(0, TaskbarTargets.Pick([Portrait, Main], @"\\.\display2"));
    }

    [Fact]
    public void Pick_WantedMonitorGone_FallsBackToMain()
    {
        Assert.Equal(0, TaskbarTargets.Pick([Main], @"\\.\DISPLAY2"));
    }

    [Fact]
    public void Pick_NoMainInTheList_TakesTheFirst_EmptyIsMinusOne()
    {
        Assert.Equal(0, TaskbarTargets.Pick([Portrait], @"\\.\DISPLAY9"));
        Assert.Equal(-1, TaskbarTargets.Pick([], null));
    }

    [Fact]
    public void MenuItems_LeftToRight_WithMainMarkedAndNullValue()
    {
        var items = TaskbarTargets.MenuItems([Main, Portrait], @"\\.\DISPLAY2");

        Assert.Equal(["Monitor 1  1080×1920", "Monitor 2 (main)  1920×1080"], items.Select(i => i.Label));
        Assert.Equal([@"\\.\DISPLAY2", null], items.Select(i => i.Value));
        Assert.Equal([true, false], items.Select(i => i.IsCurrent));
    }

    [Fact]
    public void MenuItems_WantedGone_MarksTheMainOneCurrent()
    {
        var items = TaskbarTargets.MenuItems([Main, Portrait], @"\\.\DISPLAY7");

        Assert.Equal([false, true], items.Select(i => i.IsCurrent));
    }
}
