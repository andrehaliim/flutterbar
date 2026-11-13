using FlutterBar.Core.Docking;
using FlutterBar.Core.Layout;

namespace FlutterBar.Core.Tests.Docking;

public sealed class DockingRulesTests
{
    private static readonly Box Monitor = new(0, 0, 1920, 1080);
    private static readonly Box Taskbar = new(0, 1032, 1920, 1080);

    // ---- auto-hide visibility (v3.2)

    [Fact]
    public void AutoHide_SpikeLog_29Percent_Hidden()
    {
        // `auto-hide: taskbar OFF-SCREEN ... vis=29% tbRect=(0,1066,1920,1114)`
        var bar = new Box(0, 1066, 1920, 1114);

        Assert.Equal(14.0 / 48, AutoHideVisibility.VisibleFraction(bar, Monitor), 6);
        Assert.True(AutoHideVisibility.IsHidden(bar, Monitor));
    }

    [Fact]
    public void AutoHide_FullyVisible()
    {
        Assert.Equal(1.0, AutoHideVisibility.VisibleFraction(Taskbar, Monitor));
        Assert.False(AutoHideVisibility.IsHidden(Taskbar, Monitor));
    }

    [Fact]
    public void AutoHide_ExactlyHalf_Visible()
    {
        Assert.False(AutoHideVisibility.IsHidden(new Box(0, 1056, 1920, 1104), Monitor));
    }

    [Fact]
    public void AutoHide_Parked2PxStrip_Hidden()
    {
        // Windows parks an auto-hidden bar with a 2 px sliver on screen.
        Assert.True(AutoHideVisibility.IsHidden(new Box(0, 1078, 1920, 1126), Monitor));
    }

    [Fact]
    public void AutoHide_OnOtherMonitor_Hidden()
    {
        Assert.True(AutoHideVisibility.IsHidden(new Box(1920, 1032, 3840, 1080), Monitor));
    }

    [Fact]
    public void AutoHide_DegenerateBar_Hidden()
    {
        Assert.True(AutoHideVisibility.IsHidden(new Box(0, 1080, 1920, 1080), Monitor));
    }

    // ---- manual mode (offsetFromRight)

    [Fact]
    public void Manual_SpikeObservation_Offset500()
    {
        // `{"mode":"manual","offsetFromRight":500}` -> widget at 1920-500-300 = 1120.
        Assert.Equal(1120, ManualPlacement.XFromOffset(Taskbar, 500, 300));
    }

    [Fact]
    public void Manual_HugeOffset_ClampedToTaskbarLeft()
    {
        Assert.Equal(0, ManualPlacement.XFromOffset(Taskbar, 5000, 300));
    }

    [Fact]
    public void Manual_ZeroOffset_RightEdge()
    {
        Assert.Equal(1620, ManualPlacement.XFromOffset(Taskbar, 0, 300));
    }

    [Fact]
    public void Manual_SmallerResolution_StillInside()
    {
        var small = new Box(0, 720, 1280, 768);

        Assert.Equal(0, ManualPlacement.XFromOffset(small, 1100, 300));
        Assert.Equal(480, ManualPlacement.XFromOffset(small, 500, 300));
    }

    [Fact]
    public void Manual_OffsetFromX_RoundTrips()
    {
        Assert.Equal(500, ManualPlacement.OffsetFromX(Taskbar, 1120, 300));
        Assert.Equal(0, ManualPlacement.OffsetFromX(Taskbar, 1700, 300)); // past the right edge -> 0
    }

    [Fact]
    public void Manual_ClampX()
    {
        Assert.Equal(0, ManualPlacement.ClampX(Taskbar, -40, 300));
        Assert.Equal(1620, ManualPlacement.ClampX(Taskbar, 1700, 300));
        Assert.Equal(900, ManualPlacement.ClampX(Taskbar, 900, 300));
        Assert.Equal(0, ManualPlacement.ClampX(new Box(0, 0, 200, 40), 50, 300)); // wider than the taskbar
    }

    // ---- fullscreen rule (spike v2 + SHQueryUserNotificationState)

    private static ForegroundInfo Fg(Box rect, bool shell = false, bool onTaskbarMonitor = true, bool nullOrSelf = false) =>
        new(nullOrSelf, shell, onTaskbarMonitor, rect, Monitor);

    private const int QunsAcceptsNotifications = 5;

    [Fact]
    public void Fullscreen_WindowCoveringMonitor()
    {
        Assert.True(FullscreenRule.Evaluate(false, Fg(Monitor), QunsAcceptsNotifications, previous: false));
        Assert.True(FullscreenRule.Evaluate(false, Fg(new Box(-8, -8, 1928, 1088)), QunsAcceptsNotifications, false));
    }

    [Fact]
    public void Fullscreen_MaximizedWindowAboveTaskbar_NotFullscreen()
    {
        Assert.False(FullscreenRule.Evaluate(false, Fg(new Box(0, 0, 1920, 1032)), QunsAcceptsNotifications, true));
    }

    [Fact]
    public void Fullscreen_ShellWindow_Never()
    {
        // Alt+Tab / Task View (XamlExplorerHostIslandWindow, explorer) covers the monitor but is shell.
        Assert.False(FullscreenRule.Evaluate(false, Fg(Monitor, shell: true), 3, true));
    }

    [Fact]
    public void Fullscreen_AutoHideOn_Never()
    {
        Assert.False(FullscreenRule.Evaluate(true, Fg(Monitor), 3, true));
    }

    [Fact]
    public void Fullscreen_NullOrSelfForeground_KeepsPrevious()
    {
        Assert.True(FullscreenRule.Evaluate(false, Fg(Monitor, nullOrSelf: true), 5, previous: true));
        Assert.False(FullscreenRule.Evaluate(false, Fg(Monitor, nullOrSelf: true), 5, previous: false));
    }

    [Fact]
    public void Fullscreen_ForegroundOnOtherMonitor_TaskbarMonitorNotCovered_NotFullscreen()
    {
        Assert.False(FullscreenRule.Evaluate(false, Fg(Monitor, onTaskbarMonitor: false), 3, true));
    }

    [Fact]
    public void Fullscreen_ForegroundOnOtherMonitor_TaskbarMonitorCovered_StaysFullscreen()
    {
        // Fullscreen video on the taskbar monitor, then a click on a window / the desktop of another monitor.
        var window = Fg(new Box(100, 100, 800, 600), onTaskbarMonitor: false) with { TaskbarMonitorCovered = true };
        var shell = Fg(Monitor, shell: true, onTaskbarMonitor: false) with { TaskbarMonitorCovered = true };
        Assert.True(FullscreenRule.Evaluate(false, window, QunsAcceptsNotifications, false));
        Assert.True(FullscreenRule.Evaluate(false, shell, QunsAcceptsNotifications, false));
        Assert.False(FullscreenRule.Evaluate(true, window, QunsAcceptsNotifications, true)); // auto-hide still wins
    }

    [Fact]
    public void Fullscreen_DesktopForeground_TaskbarMonitorCoverageDecides()
    {
        // The desktop (Progman) spans every monitor and is reported on the primary one wherever the click landed:
        // fullscreen video on the taskbar monitor, then a click on the desktop of another monitor.
        var desktop = Fg(new Box(-1080, 0, 1920, 1920), shell: true) with { IsDesktop = true };
        Assert.True(FullscreenRule.Evaluate(false, desktop with { TaskbarMonitorCovered = true }, QunsAcceptsNotifications, false));
        Assert.False(FullscreenRule.Evaluate(false, desktop, QunsAcceptsNotifications, true));
        Assert.False(FullscreenRule.Evaluate(true, desktop with { TaskbarMonitorCovered = true }, QunsAcceptsNotifications, true)); // auto-hide still wins
    }

    [Theory]
    [InlineData(2, true)] // QUNS_BUSY
    [InlineData(3, true)] // QUNS_RUNNING_D3D_FULL_SCREEN
    [InlineData(5, false)] // QUNS_ACCEPTS_NOTIFICATIONS
    [InlineData(7, false)] // QUNS_APP (seen while Start/Search is open)
    public void Fullscreen_NotificationState(int quns, bool expected)
    {
        Assert.Equal(expected, FullscreenRule.Evaluate(false, Fg(new Box(100, 100, 800, 600)), quns, false));
    }

    // ---- shell classification

    [Theory]
    [InlineData("Shell_TrayWnd", null, true)]
    [InlineData("Progman", "whatever", true)]
    [InlineData("XamlExplorerHostIslandWindow", "explorer", true)]
    [InlineData("Windows.UI.Core.CoreWindow", "SearchHost", true)]
    [InlineData("Windows.UI.Core.CoreWindow", "StartMenuExperienceHost", true)]
    [InlineData("Windows.UI.Core.CoreWindow", "ShellExperienceHost", true)]
    [InlineData("ControlCenterWindow", "ShellHost", true)]
    [InlineData("Chrome_WidgetWin_1", "chrome", false)]
    [InlineData("Chrome_WidgetWin_1", null, true)] // lookup failed -> treated as shell
    public void Shell_ClassOrProcess(string cls, string? process, bool expected)
    {
        Assert.Equal(expected, ShellWindowRules.IsShell(cls, process));
    }

    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("Shell_TrayWnd", false)]
    [InlineData("XamlExplorerHostIslandWindow", false)]
    [InlineData("Chrome_WidgetWin_1", false)]
    public void Shell_Desktop(string cls, bool expected)
    {
        Assert.Equal(expected, ShellWindowRules.IsDesktop(cls));
    }

    // ---- preferred size and widths

    [Theory]
    [InlineData("full", WidgetSize.Full)]
    [InlineData("Compact", WidgetSize.Compact)]
    [InlineData("buttonsOnly", WidgetSize.ButtonsOnly)]
    [InlineData("buttons", WidgetSize.ButtonsOnly)]
    [InlineData(null, WidgetSize.Full)]
    [InlineData("nonsense", WidgetSize.Full)]
    public void SizeNames_Parse(string? name, WidgetSize expected)
    {
        Assert.Equal(expected, WidgetSizeNames.Parse(name));
    }

    [Fact]
    public void SizeNames_RoundTrip()
    {
        foreach (var size in Enum.GetValues<WidgetSize>())
        {
            Assert.Equal(size, WidgetSizeNames.Parse(WidgetSizeNames.ToSettingName(size)));
        }
    }
}
