using StatefulBar.Core.Docking;
using StatefulBar.Core.Layout;

namespace StatefulBar.Core.Tests.Docking;

public sealed class TaskbarLayoutAnalyzerTests
{
    // Numbers from the findings doc (Win11, 1920x1080, DPI 96, taskbar 48 px high).
    private static readonly Box Taskbar = new(0, 1032, 1920, 1080);

    private static TaskbarElement El(string cls, string id, double left, double right, string ctype = "ControlType.Button", string process = "explorer", double top = 1032, double bottom = 1080) =>
        new(cls, id, ctype, process, new Box(left, top, right, bottom));

    private static TaskbarElement Start => El("ToggleButton", "StartButton", 784, 829);

    private static TaskbarElement AppButton(double left, double right) =>
        El("Taskbar.TaskListButtonAutomationPeer", "Appid: Chrome", left, right);

    private static TaskbarElement TrayIcon(double left, double right) =>
        El("SystemTray.NormalButton", "SystemTrayIcon", left, right);

    private static ElementVerdict Classify(TaskbarElement e, double trayLeft = 1610) =>
        TaskbarElementClassifier.Classify(e, Taskbar, trayLeft);

    [Fact]
    public void Classify_StartAndAppButtons_Counted()
    {
        Assert.Equal(ElementRole.Occupied, Classify(Start).Role);
        Assert.Equal(ElementRole.Occupied, Classify(AppButton(829, 873)).Role);
    }

    [Fact]
    public void Classify_TrayElements_Tray()
    {
        Assert.Equal(ElementRole.Tray, Classify(TrayIcon(1610, 1634)).Role);
        Assert.Equal(ElementRole.Tray, Classify(El("Button", "NotifyItemIcon", 1640, 1664)).Role);
        Assert.Equal(ElementRole.Tray, Classify(El("SystemTray.OmniButton", "", 1834, 1904)).Role);
    }

    [Fact]
    public void Classify_OutsideTaskbarRect_Ignored()
    {
        // Thumbnails/flyouts are drawn ABOVE the taskbar: rule (a) excludes them.
        var thumb = El("Taskbar.ThumbnailPeer", "", 900, 1100, top: 850, bottom: 1030);

        var verdict = Classify(thumb);

        Assert.Equal(ElementRole.Ignored, verdict.Role);
        Assert.Contains("outside", verdict.Reason);
    }

    [Fact]
    public void Classify_TwoPixelSlackStillInside()
    {
        Assert.Equal(ElementRole.Occupied, Classify(El("ToggleButton", "StartButton", 784, 829, top: 1030, bottom: 1082)).Role);
        Assert.Equal(ElementRole.Ignored, Classify(El("ToggleButton", "StartButton", 784, 829, top: 1029, bottom: 1080)).Role);
    }

    [Fact]
    public void Classify_LowElement_Ignored()
    {
        var verdict = Classify(El("TextBlock", "", 900, 950, top: 1050, bottom: 1070)); // 20 < 24 (50% of 48)

        Assert.Equal(ElementRole.Ignored, verdict.Role);
        Assert.Contains("height", verdict.Reason);
    }

    [Fact]
    public void Classify_NarrowElement_Ignored()
    {
        var verdict = Classify(El("Indicator", "", 900, 907));

        Assert.Equal(ElementRole.Ignored, verdict.Role);
        Assert.Contains("width", verdict.Reason);
    }

    [Theory]
    [InlineData("FlutterBar.App")]
    [InlineData("flutterbar.app")]
    [InlineData("DockingSpike")]
    public void Classify_OurWidgetProcesses_Ignored(string process)
    {
        var verdict = Classify(El("Window", "", 1202, 1502, ctype: "ControlType.Window", process: process));

        Assert.Equal(ElementRole.Ignored, verdict.Role);
        Assert.Contains(process, verdict.Reason);
    }

    [Theory]
    [InlineData("ToolTip", "ControlType.ToolTip")]
    [InlineData("TaskListThumbnailWnd", "ControlType.Window")]
    [InlineData("Xaml_WindowedPopupClass", "ControlType.Window")]
    public void Classify_Transient_Ignored(string cls, string ctype)
    {
        var verdict = Classify(El(cls, "", 900, 1000, ctype: ctype));

        Assert.Equal(ElementRole.Ignored, verdict.Role);
        Assert.Contains("transient", verdict.Reason);
    }

    [Fact]
    public void Classify_WideContainerAndLegacyWindows_Ignored()
    {
        Assert.Equal(ElementRole.Ignored, Classify(El("Taskbar.TaskbarFrameAutomationPeer", "TaskbarFrame", 700, 1150)).Role);
        Assert.Equal(ElementRole.Ignored, Classify(El("ReBarWindow32", "", 828, 1092)).Role);
        Assert.Equal(ElementRole.Ignored, Classify(El("Pane", "", 0, 1000)).Role); // >= 50% of the taskbar width
        Assert.Equal(ElementRole.Ignored, Classify(El("Image", "", 900, 940)).Role);
    }

    [Fact]
    public void Classify_OverlappingTray_Ignored()
    {
        var verdict = Classify(El("Button", "", 1600, 1620), trayLeft: 1610);

        Assert.Equal(ElementRole.Ignored, verdict.Role);
        Assert.Contains("tray", verdict.Reason);
    }

    [Fact]
    public void Classify_ExplorerNonButtonNearTray_Counted()
    {
        // The weather "Widgets" button near the tray is explorer-owned and must count (v3.1: no button whitelist).
        var verdict = Classify(El("ToggleButton", "WidgetsButton", 1510, 1662), trayLeft: 1674);

        Assert.Equal(ElementRole.Occupied, verdict.Role);
    }

    [Fact]
    public void Analyze_Normal_TrayLeftAndSortedOccupied()
    {
        var elements = new[] { AppButton(829, 873), Start, TrayIcon(1610, 1634), El("SystemTray.OmniButton", "SystemTrayIcon", 1834, 1904) };

        var result = TaskbarLayoutAnalyzer.Analyze(Taskbar, elements, trayNotifyLeft: 1610);

        Assert.True(result.IsValid);
        Assert.Null(result.RejectReason);
        Assert.Equal(1610, result.TrayLeft);
        Assert.False(result.TrayFromTrayNotifyWnd);
        Assert.Equal([784.0, 829.0], result.Occupied.Select(b => b.Left));
        Assert.Equal(elements.Length, result.Verdicts.Count);
    }

    [Fact]
    public void Analyze_NoTrayElements_Rejected()
    {
        // Observed while Start is open: UIA returns no tray elements.
        var result = TaskbarLayoutAnalyzer.Analyze(Taskbar, [Start], trayNotifyLeft: 1674);

        Assert.False(result.IsValid);
        Assert.Contains("tray", result.RejectReason);
    }

    [Fact]
    public void Analyze_SecondaryWithClockHidden_UsesTheRightEdge()
    {
        // Spike 2026-11-09: a secondary taskbar has no TrayNotifyWnd and only a clock, which can be hidden.
        var result = TaskbarLayoutAnalyzer.Analyze(Taskbar, [Start, AppButton(829, 873)], trayNotifyLeft: null, trayOptional: true);

        Assert.True(result.IsValid);
        Assert.Equal(Taskbar.Right, result.TrayLeft);
        Assert.Equal(2, result.Occupied.Count);
    }

    [Fact]
    public void Analyze_SecondaryWithClock_UsesTheClock()
    {
        var clock = El("SystemTray.OmniButton", "SystemTrayIcon", 1842, 1912);

        var result = TaskbarLayoutAnalyzer.Analyze(Taskbar, [Start, clock], trayNotifyLeft: null, trayOptional: true);

        Assert.True(result.IsValid);
        Assert.Equal(1842, result.TrayLeft);
    }

    [Fact]
    public void Analyze_SecondaryStillNeedsOccupiedElements()
    {
        var result = TaskbarLayoutAnalyzer.Analyze(Taskbar, [], trayNotifyLeft: null, trayOptional: true);

        Assert.False(result.IsValid);
        Assert.Contains("occupied", result.RejectReason);
    }

    [Fact]
    public void Analyze_NoOccupied_Rejected()
    {
        var result = TaskbarLayoutAnalyzer.Analyze(Taskbar, [TrayIcon(1674, 1700)], trayNotifyLeft: 1674);

        Assert.False(result.IsValid);
        Assert.Contains("occupied", result.RejectReason);
    }

    [Fact]
    public void Analyze_Empty_Rejected()
    {
        Assert.False(TaskbarLayoutAnalyzer.Analyze(Taskbar, [], trayNotifyLeft: null).IsValid);
    }

    [Fact]
    public void Analyze_TrayMismatchOver300_UsesTrayNotifyWnd()
    {
        var result = TaskbarLayoutAnalyzer.Analyze(Taskbar, [Start, TrayIcon(1200, 1224)], trayNotifyLeft: 1674);

        Assert.True(result.TrayFromTrayNotifyWnd);
        Assert.Equal(1674, result.TrayLeft);
    }

    [Fact]
    public void Analyze_TrayMismatchUpTo300_KeepsUia()
    {
        var result = TaskbarLayoutAnalyzer.Analyze(Taskbar, [Start, TrayIcon(1374, 1400)], trayNotifyLeft: 1674);

        Assert.False(result.TrayFromTrayNotifyWnd);
        Assert.Equal(1374, result.TrayLeft);
    }
}
