using FlutterBar.Core.Layout;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Settings;

namespace FlutterBar.Core.Tests.Orchestration;

public sealed class MenuRulesTests
{
    [Theory]
    [InlineData(WidgetSize.Full, "Full")]
    [InlineData(WidgetSize.Compact, "Compact")]
    [InlineData(WidgetSize.ButtonsOnly, "Buttons only")]
    public void SizeLabels_AreSpecCopy(WidgetSize size, string label) => Assert.Equal(label, WidgetSizeMenu.Label(size));

    [Fact]
    public void ChangeSize_WritesSettingName_AndDropsWidthOverride()
    {
        var widget = new WidgetSettings { Size = "full", WidthOverride = 480 };

        Assert.True(WidgetSizeMenu.Apply(widget, WidgetSize.Compact));

        Assert.Equal("compact", widget.Size);
        Assert.Null(widget.WidthOverride);
    }

    [Fact]
    public void SameSize_KeepsWidthOverride()
    {
        var widget = new WidgetSettings { Size = "full", WidthOverride = 480 };

        Assert.False(WidgetSizeMenu.Apply(widget, WidgetSize.Full));

        Assert.Equal(480, widget.WidthOverride);
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
        Assert.Equal("FlutterBar hit an error. Details are in diag.log", UiText.UnexpectedError);
        Assert.Equal("Open a new terminal to pick up the PATH change", UiText.PathAdded);
    }
}
