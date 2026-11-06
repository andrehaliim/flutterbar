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
    [InlineData("a!PATH!b")]
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
        Assert.Equal("/e:ON /v:OFF /d /s /c \"\"C:\\x\\a.bat\" plain\"", CmdEscaper.BuildCmdArguments(@"C:\x\a.bat", ["plain"]));
    }

    [Theory]
    [InlineData("dir with space")]
    [InlineData("pct%OS%dir")]
    [InlineData("aplikasi-\u00fc \u043f\u0440\u043e\u0435\u043a\u0442")]
    public async Task BatchPath_WithSpecialChars_Works(string dirName)
    {
        using var echo = new EchoArgsFixture(dirName);
        var result = await new ProcessRunner(new ProcessLauncher())
            .RunAsync(echo.Spec(echo.Cmd, "x y"), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["x y"], EchoArgsFixture.ParseArgs(result.Stdout));
    }

    [Fact]
    public void BatchPath_WithQuote_Rejected()
    {
        Assert.Throws<ArgumentException>(() => CmdEscaper.BuildCmdArguments("C:\\x\"y\\a.bat", []));
    }
}
