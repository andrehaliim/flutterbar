using StatefulBar.Core.Layout;
using StatefulBar.Core.Orchestration;
using StatefulBar.Core.Sessions;
using StatefulBar.Core.Settings;

namespace StatefulBar.Core.Tests.Orchestration;

public sealed class MenuRulesTests
{
    [Theory]
    [InlineData(WidgetSize.Full, "Full")]
    [InlineData(WidgetSize.Compact, "Compact")]
    [InlineData(WidgetSize.ButtonsOnly, "Buttons only")]
    public void SizeLabels_AreSpecCopy(WidgetSize size, string label) => Assert.Equal(label, WidgetSizeMenu.Label(size));

    [Fact]
    public void ChangeSize_WritesSettingName()
    {
        var widget = new WidgetSettings { Size = "full" };

        Assert.True(WidgetSizeMenu.Apply(widget, WidgetSize.Compact));

        Assert.Equal("compact", widget.Size);
    }

    [Fact]
    public void NoRoomNote_AllFit_Null()
    {
        Assert.Null(WidgetSizeMenu.NoRoomNote([WidgetSize.Full, WidgetSize.Compact, WidgetSize.ButtonsOnly]));
    }

    [Fact]
    public void NoRoomNote_NamesTheSizesThatDoNotFit()
    {
        Assert.Equal("Not enough taskbar space for Full", WidgetSizeMenu.NoRoomNote([WidgetSize.Compact, WidgetSize.ButtonsOnly]));
        Assert.Equal("Not enough taskbar space for Full and Compact", WidgetSizeMenu.NoRoomNote([WidgetSize.ButtonsOnly]));
    }

    [Fact]
    public void SameSize_ChangesNothing()
    {
        var widget = new WidgetSettings { Size = "full" };

        Assert.False(WidgetSizeMenu.Apply(widget, WidgetSize.Full));
    }

    [Fact]
    public void ChangeSize_ToButtonsOnly_UsesSettingName()
    {
        var widget = new WidgetSettings();

        WidgetSizeMenu.Apply(widget, WidgetSize.ButtonsOnly);

        Assert.Equal("buttonsOnly", widget.Size);
        Assert.Equal(WidgetSize.ButtonsOnly, WidgetSizeNames.Parse(widget.Size));
    }

    [Theory]
    [InlineData(SessionState.Idle, false)]
    [InlineData(SessionState.Failed, false)]
    [InlineData(SessionState.Starting, true)]
    [InlineData(SessionState.Running, true)]
    [InlineData(SessionState.Reloading, true)]
    [InlineData(SessionState.Stopping, true)]
    public void Exit_AsksOnlyWithActiveSession(SessionState state, bool asks) => Assert.Equal(asks, ExitFlow.NeedsConfirmation(state));

    [Fact]
    public void ExitAndErrorCopy()
    {
        Assert.Equal("Stop the session and exit?", UiText.ConfirmExit);
        Assert.Equal("StatefulBar hit an error. Details are in diag.log", UiText.UnexpectedError);
        Assert.Equal("Open a new terminal to pick up the PATH change", UiText.PathAdded);
    }

    [Fact]
    public void SystemMenu_Unpackaged_KeepsTheRegistryBackedItems()
    {
        var menu = SystemMenu.For(packaged: false);

        Assert.True(menu.ShowAddCliToPath);
        Assert.True(menu.StartupIsToggle);
        Assert.Equal("Start with Windows", menu.StartupHeader);
        Assert.Null(menu.StartupToolTip);
        Assert.True(menu.ShowUpdates);
    }

    [Fact]
    public void SystemMenu_Packaged_HidesPathAndLeavesStartupToWindows()
    {
        var menu = SystemMenu.For(packaged: true);

        Assert.False(menu.ShowAddCliToPath); // sbar comes from the package's execution alias
        Assert.False(menu.StartupIsToggle); // a package cannot write the real HKCU Run key
        Assert.Equal("Start with Windows…", menu.StartupHeader);
        Assert.Equal("Opens the Windows startup settings, where StatefulBar is turned on or off", menu.StartupToolTip);
        Assert.False(menu.ShowUpdates); // the Store updates the package
    }

    [Fact]
    public void UpdateCopy()
    {
        Assert.Equal("StatefulBar 1.0.2 is available", UiText.UpdateAvailable(new Version(1, 0, 2)));
        Assert.Equal("You're on the latest version (1.0.1)", UiText.UpToDate(new Version(1, 0, 1, 0)));
        Assert.Equal("Couldn't check for updates: timed out", UiText.UpdateCheckFailed("timed out"));
        Assert.Equal("View release", UiText.ViewRelease);
    }
}
