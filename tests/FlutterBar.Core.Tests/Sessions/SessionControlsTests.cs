using FlutterBar.Core.Config;
using FlutterBar.Core.Processes;
using FlutterBar.Core.Sessions;

namespace FlutterBar.Core.Tests.Sessions;

public sealed class SessionControlsTests
{
    private static RunRequest Request(RunMode mode) =>
        new("C:\\Work\\app", new LaunchConfig("beta", "lib/main.dart", null, mode, []), "chrome", new ProcessSpec("flutter", [], "C:\\Work\\app"));

    private static SessionSnapshot Snap(
        SessionState state,
        bool hasError = false,
        string? progress = null,
        string? failure = null,
        bool canReload = false,
        bool canRestart = false,
        RunMode mode = RunMode.Debug,
        int errorCount = 0) =>
        new(state, hasError, errorCount, progress, failure, canReload, canRestart, state == SessionState.Idle ? null : Request(mode));

    [Fact]
    public void Idle_GreyDot_RunOnly()
    {
        var c = SessionControls.From(Snap(SessionState.Idle));

        Assert.Equal(StatusDot.Idle, c.Dot);
        Assert.True(c.ShowRun);
        Assert.False(c.ShowReload || c.ShowRestart || c.ShowStop || c.ShowProgress);
    }

    [Fact]
    public void Starting_BlinkingYellow_ProgressAndStop()
    {
        var c = SessionControls.From(Snap(SessionState.Starting, progress: "Running Gradle task 'assembleDebug'..."));

        Assert.Equal(StatusDot.Starting, c.Dot);
        Assert.True(c.ShowProgress);
        Assert.True(c.ShowStop && c.StopEnabled);
        Assert.False(c.ShowRun || c.ShowReload || c.ShowRestart);
        Assert.Equal("Running Gradle task 'assembleDebug'...", c.StatusText);
    }

    [Fact]
    public void Starting_NoProgressYet_DefaultText()
    {
        Assert.Equal("Memulai…", SessionControls.From(Snap(SessionState.Starting)).StatusText);
    }

    [Fact]
    public void Running_Debug_AllEnabled()
    {
        var c = SessionControls.From(Snap(SessionState.Running, canReload: true, canRestart: true));

        Assert.Equal(StatusDot.Running, c.Dot);
        Assert.False(c.ShowRun);
        Assert.True(c.ShowReload && c.ReloadEnabled);
        Assert.True(c.ShowRestart && c.RestartEnabled);
        Assert.True(c.ShowStop && c.StopEnabled);
        Assert.Equal("Hot reload", c.ReloadToolTip);
        Assert.Equal("Hot restart", c.RestartToolTip);
    }

    [Theory]
    [InlineData(RunMode.Profile)]
    [InlineData(RunMode.Release)]
    public void Running_ProfileOrRelease_ReloadRestartDisabledWithTooltip(RunMode mode)
    {
        var c = SessionControls.From(Snap(SessionState.Running, mode: mode));

        Assert.True(c.ShowReload);
        Assert.False(c.ReloadEnabled);
        Assert.False(c.RestartEnabled);
        Assert.Equal("Tidak didukung di mode release/profile", c.ReloadToolTip);
        Assert.Equal("Tidak didukung di mode release/profile", c.RestartToolTip);
    }

    [Fact]
    public void Running_ReloadRejectedByDevice_Tooltip()
    {
        var c = SessionControls.From(Snap(SessionState.Running, canReload: false, canRestart: true));

        Assert.False(c.ReloadEnabled);
        Assert.Equal("Hot reload tidak didukung di device ini", c.ReloadToolTip);
        Assert.True(c.RestartEnabled);
    }

    [Fact]
    public void Running_NoRestartSupport_Tooltip()
    {
        var c = SessionControls.From(Snap(SessionState.Running, canReload: true, canRestart: false));

        Assert.False(c.RestartEnabled);
        Assert.Equal("Hot restart tidak didukung di device ini", c.RestartToolTip);
    }

    [Fact]
    public void Running_ErrorFlag_RedDot()
    {
        var c = SessionControls.From(Snap(SessionState.Running, hasError: true, errorCount: 3, canReload: true, canRestart: true));

        Assert.Equal(StatusDot.Error, c.Dot);
        Assert.Contains("3 error", c.StatusText);
    }

    [Fact]
    public void Reloading_ButtonsShownButBusy()
    {
        var c = SessionControls.From(Snap(SessionState.Reloading));

        Assert.Equal(StatusDot.Running, c.Dot);
        Assert.True(c.ShowReload && c.ShowRestart && c.ShowStop);
        Assert.False(c.ReloadEnabled || c.RestartEnabled);
        Assert.Equal("Sedang reload", c.ReloadToolTip);
        Assert.True(c.StopEnabled);
    }

    [Fact]
    public void Stopping_NoButtons()
    {
        var c = SessionControls.From(Snap(SessionState.Stopping));

        Assert.Equal(StatusDot.Stopping, c.Dot);
        Assert.False(c.ShowRun || c.ShowReload || c.ShowRestart || c.ShowStop);
    }

    [Fact]
    public void Failed_RedDot_RunAndMessage()
    {
        var c = SessionControls.From(Snap(SessionState.Failed, failure: "Build gagal"));

        Assert.Equal(StatusDot.Error, c.Dot);
        Assert.True(c.ShowRun);
        Assert.Equal("Build gagal", c.StatusText);
    }
}
