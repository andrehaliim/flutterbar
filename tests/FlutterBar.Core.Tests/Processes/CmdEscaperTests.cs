using FlutterBar.Core.Processes;

namespace FlutterBar.Core.Tests.Processes;

public class CmdEscaperTests
{
    [Theory]
    [InlineData("--dart-define=APP_NAME=GTS Mobile Beta")]
    [InlineData("--dart-define=URL=https://x.id/?a=1&b=2")]
    [InlineData("100%")]
    [InlineData("%PATH%")]
    [InlineData("a\"b")]
    [InlineData("^caret|pipe<lt>gt")]
    [InlineData("")]
    [InlineData("trailing\\")]
    public async Task BatchArgs_ArriveVerbatim(string arg)
    {
        using var echo = new EchoArgsFixture();
        var result = await new ProcessRunner(new ProcessLauncher())
            .RunAsync(echo.Spec(echo.Cmd, arg), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([arg], EchoArgsFixture.ParseArgs(result.Stdout));
    }

    [Theory]
    [InlineData("two words\\")]
    [InlineData("a\\\"b c")]
    [InlineData("say \"hi\" & bye")]
    [InlineData("x\" & echo INJECTED & \"y")]
    [InlineData("(paren) ! bang")]
    [InlineData("semi;colon,comma=eq")]
    [InlineData("`tick`")]
    [InlineData("%CD% 50%%")]
    [InlineData("tab\tsep")]
    [InlineData("C:\\Users\\Andre\\My Projects\\aplikasi\\")]
    public async Task BatchArgs_MoreTrickyCases_ArriveVerbatim(string arg)
    {
        using var echo = new EchoArgsFixture();
        var result = await new ProcessRunner(new ProcessLauncher())
            .RunAsync(echo.Spec(echo.Cmd, "first", arg, "last"), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(["first", arg, "last"], EchoArgsFixture.ParseArgs(result.Stdout));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void BatchArgs_NewlineRejected(string arg)
    {
        Assert.Throws<ArgumentException>(() => CmdEscaper.BuildCmdArguments(@"C:\x\a.bat", [arg]));
    }

    [Fact]
    public void BuildCmdArguments_WrapsWholeCommandInOuterQuotes()
    {
        Assert.Equal("/d /s /c \"\"C:\\x\\a.bat\" plain\"", CmdEscaper.BuildCmdArguments(@"C:\x\a.bat", ["plain"]));
    }

    [Fact]
    public async Task BatchPath_WithSpaces_Works()
    {
        using var echo = new EchoArgsFixture("dir with space");
        var result = await new ProcessRunner(new ProcessLauncher())
            .RunAsync(echo.Spec(echo.Cmd, "x y"), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(["x y"], EchoArgsFixture.ParseArgs(result.Stdout));
    }
}
