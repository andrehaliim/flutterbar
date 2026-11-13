using StatefulBar.Core.Layout;

namespace StatefulBar.Core.Tests.Layout;

public sealed class TaskbarGeometryTests
{
    private static readonly Box Taskbar = new(0, 1040, 1920, 1080);
    private static readonly Box Tray = new(1700, 1040, 1920, 1080);

    [Fact]
    public void FreeArea_CenteredTaskbar()
    {
        var buttons = new[] { new Box(800, 1040, 1100, 1080) };

        var area = TaskbarGeometry.FreeAreaLeftOfTray(Taskbar, Tray, buttons);

        Assert.Equal(new Box(1108, 1040, 1692, 1080), area);
    }

    [Fact]
    public void FreeArea_LeftAlignedTaskbar()
    {
        var buttons = new[] { new Box(0, 1040, 300, 1080) };

        var area = TaskbarGeometry.FreeAreaLeftOfTray(Taskbar, Tray, buttons);

        Assert.Equal(new Box(308, 1040, 1692, 1080), area);
    }

    [Fact]
    public void FreeArea_NoGap_Null()
    {
        var buttons = new[] { new Box(800, 1040, 1690, 1080) };

        Assert.Null(TaskbarGeometry.FreeAreaLeftOfTray(Taskbar, Tray, buttons));
    }

    [Fact]
    public void FreeArea_NoButtons_StartsAtTaskbarLeft()
    {
        var area = TaskbarGeometry.FreeAreaLeftOfTray(Taskbar, Tray, Array.Empty<Box>());

        Assert.Equal(new Box(8, 1040, 1692, 1080), area);
    }

    [Fact]
    public void FreeArea_IgnoresBoxesRightOfTray()
    {
        var buttons = new[]
        {
            new Box(800, 1040, 1100, 1080),
            new Box(1750, 1040, 1800, 1080),
        };

        var area = TaskbarGeometry.FreeAreaLeftOfTray(Taskbar, Tray, buttons);

        Assert.Equal(new Box(1108, 1040, 1692, 1080), area);
    }

    [Fact]
    public void FreeArea_IgnoresBoxesOutsideVerticalSpan()
    {
        var buttons = new[]
        {
            new Box(800, 1040, 1100, 1080),
            new Box(1200, 900, 1400, 1030),
        };

        var area = TaskbarGeometry.FreeAreaLeftOfTray(Taskbar, Tray, buttons);

        Assert.Equal(new Box(1108, 1040, 1692, 1080), area);
    }
}
