using FlutterBar.Core.Docking;
using FlutterBar.Core.Layout;

namespace FlutterBar.Core.Tests.Docking;

public sealed class PlacementPlannerTests
{
    private static readonly Box Taskbar = new(0, 1032, 1920, 1080);

    // Spike widths (logical px at DPI 96): Full 300, Compact 260, ButtonsOnly 120.
    private static readonly IReadOnlyDictionary<WidgetSize, double> Widths = new Dictionary<WidgetSize, double>
    {
        [WidgetSize.Full] = 300,
        [WidgetSize.Compact] = 260,
        [WidgetSize.ButtonsOnly] = 120,
    };

    private static Box Gap(double left, double right) => new(left, Taskbar.Top, right, Taskbar.Bottom);

    // v3 user log 17:39:03: occupied up to 587, [793..1210], WidgetsButton [1510..1662], tray at 1674.
    private static readonly Box[] UserOccupied =
    [
        new(2, 1032, 587, 1080),
        new(793, 1032, 1210, 1080),
        new(1510, 1032, 1662, 1080),
    ];

    [Fact]
    public void FreeGaps_UserLog_TwoGapsInsetBy8()
    {
        var gaps = TaskbarGeometry.FreeGaps(Taskbar, trayLeft: 1674, UserOccupied);

        Assert.Equal([Gap(595, 785), Gap(1218, 1502)], gaps);
    }

    [Fact]
    public void FreeGaps_UnsortedAndOverlappingBoxes_Merged()
    {
        var occupied = new Box[] { new(900, 1032, 1000, 1080), new(784, 1032, 829, 1080), new(820, 1032, 905, 1080) };

        var gaps = TaskbarGeometry.FreeGaps(Taskbar, trayLeft: 1610, occupied);

        Assert.Equal([Gap(8, 776), Gap(1008, 1602)], gaps);
    }

    [Fact]
    public void FreeGaps_LastGapEqualsFreeAreaLeftOfTray()
    {
        // Probe v1: Start [784..829], app buttons up to 1137, tray at 1610 -> free area [1145..1602].
        var occupied = new Box[] { new(784, 1032, 829, 1080), new(829, 1032, 1137, 1080) };
        var tray = new Box(1610, 1032, 1920, 1080);

        var gaps = TaskbarGeometry.FreeGaps(Taskbar, trayLeft: 1610, occupied);

        Assert.Equal(Gap(1145, 1602), gaps[^1]);
        Assert.Equal(TaskbarGeometry.FreeAreaLeftOfTray(Taskbar, tray, occupied), gaps[^1]);
    }

    [Fact]
    public void FreeGaps_NoRoom_Empty()
    {
        var occupied = new Box[] { new(0, 1032, 1660, 1080) };

        Assert.Empty(TaskbarGeometry.FreeGaps(Taskbar, trayLeft: 1674, occupied));
    }

    [Fact]
    public void FreeGaps_IgnoresBoxesAtOrRightOfTray()
    {
        var occupied = new Box[] { new(784, 1032, 829, 1080), new(1700, 1032, 1750, 1080) };

        Assert.Equal([Gap(8, 776), Gap(837, 1666)], TaskbarGeometry.FreeGaps(Taskbar, trayLeft: 1674, occupied));
    }

    [Fact]
    public void Choose_UserLog_CompactInRightmostGap()
    {
        // [595..785]=190 [1218..1502]=284 -> Full 300 does not fit, Compact 260 <= 284 -> Compact at 1502-260.
        var gaps = new[] { Gap(595, 785), Gap(1218, 1502) };

        var p = PlacementPlanner.Choose(gaps, WidgetSize.Full, Widths, Taskbar, lastValidX: null, trayNotifyLeft: 1674);

        Assert.Equal(new Placement(WidgetSize.Compact, 1242, NoFit: false, Gap(1218, 1502)), p);
    }

    [Fact]
    public void Choose_FullFits_RightmostFittingGapWins()
    {
        // v3 auto observation: gap [595..1502] -> Full at 1202.
        var gaps = new[] { Gap(8, 400), Gap(595, 1502) };

        var p = PlacementPlanner.Choose(gaps, WidgetSize.Full, Widths, Taskbar, null, 1674);

        Assert.Equal(WidgetSize.Full, p.Size);
        Assert.Equal(1202, p.X);
        Assert.False(p.NoFit);
    }

    [Fact]
    public void Choose_RightmostGapWinsOverLargerSizeInLeftGap()
    {
        // Spec §7.3 "tepat di kiri area tray/jam": Full fits only the left gap, but the right gap (270) takes Compact (260).
        var gaps = new[] { Gap(100, 450), Gap(1300, 1570) };

        var p = PlacementPlanner.Choose(gaps, WidgetSize.Full, Widths, Taskbar, null, 1674);

        Assert.Equal(new Placement(WidgetSize.Compact, 1310, false, Gap(1300, 1570)), p);
    }

    [Fact]
    public void Choose_ProductionLog_CenteredTaskbar_CompactNearTray()
    {
        // Task 13 launch: gaps [166..663]=497 [1257..1666]=409, measured Full 437 / Compact 271 -> Compact at 1666-271.
        var widths = new Dictionary<WidgetSize, double>
        {
            [WidgetSize.Full] = 437,
            [WidgetSize.Compact] = 271,
            [WidgetSize.ButtonsOnly] = 62,
        };
        var gaps = new[] { Gap(166, 663), Gap(1257, 1666) };

        var p = PlacementPlanner.Choose(gaps, WidgetSize.Full, widths, Taskbar, null, 1674);

        Assert.Equal(new Placement(WidgetSize.Compact, 1395, false, Gap(1257, 1666)), p);
    }

    [Fact]
    public void Choose_RightGapTooSmallEvenForButtons_LeftGapWithItsLargestSize()
    {
        var gaps = new[] { Gap(100, 450), Gap(1500, 1600) }; // right gap 100 < ButtonsOnly 120

        var p = PlacementPlanner.Choose(gaps, WidgetSize.Full, Widths, Taskbar, null, 1674);

        Assert.Equal(new Placement(WidgetSize.Full, 150, false, Gap(100, 450)), p);
    }

    [Fact]
    public void Choose_RespectsPreferredSize()
    {
        var p = PlacementPlanner.Choose([Gap(595, 1502)], WidgetSize.Compact, Widths, Taskbar, null, 1674);

        Assert.Equal(WidgetSize.Compact, p.Size);
        Assert.Equal(1242, p.X);
    }

    [Fact]
    public void Choose_OnlyButtonsOnlyFits()
    {
        var p = PlacementPlanner.Choose([Gap(1372, 1502)], WidgetSize.Full, Widths, Taskbar, null, 1674);

        Assert.Equal(new Placement(WidgetSize.ButtonsOnly, 1382, false, Gap(1372, 1502)), p);
    }

    [Fact]
    public void Choose_NothingFits_KeepsLastValidXInButtonsOnly()
    {
        var p = PlacementPlanner.Choose([Gap(1452, 1502)], WidgetSize.Full, Widths, Taskbar, lastValidX: 1242, trayNotifyLeft: 1674);

        Assert.Equal(new Placement(WidgetSize.ButtonsOnly, 1242, NoFit: true, null), p);
    }

    [Fact]
    public void Choose_NothingFitsNoLastX_LeftOfTrayNotifyWnd()
    {
        // Spike --force-min-gap=50: ButtonsOnly at 1546 = 1674 - 8 - 120.
        var p = PlacementPlanner.Choose([], WidgetSize.Full, Widths, Taskbar, null, trayNotifyLeft: 1674);

        Assert.Equal(new Placement(WidgetSize.ButtonsOnly, 1546, true, null), p);
    }

    [Fact]
    public void Choose_NothingFitsNoTrayNotifyWnd_RightEdge()
    {
        var p = PlacementPlanner.Choose([], WidgetSize.Full, Widths, Taskbar, null, trayNotifyLeft: null);

        Assert.Equal(1800, p.X);
        Assert.True(p.NoFit);
    }

    [Fact]
    public void Choose_NothingFits_LastValidXClampedToTaskbar()
    {
        var p = PlacementPlanner.Choose([], WidgetSize.Full, Widths, Taskbar, lastValidX: 1900, trayNotifyLeft: 1674);

        Assert.Equal(1800, p.X);
    }

    [Fact]
    public void Fallback_NoLayoutYet_LeftOfTrayNotifyWnd()
    {
        Assert.Equal(1366, PlacementPlanner.FallbackX(Taskbar, trayNotifyLeft: 1674, width: 300));
        Assert.Equal(1620, PlacementPlanner.FallbackX(Taskbar, trayNotifyLeft: null, width: 300));
    }
}
