using FlutterBar.Core.Daemon;

namespace FlutterBar.Core.Tests.Daemon;

public class DaemonProtocolTests
{
    private static List<DaemonMessage> Parse(string fixture) =>
        File.ReadAllLines(TestPaths.Fixture("daemon", "3.22.3", fixture + ".jsonl"))
            .SelectMany(DaemonProtocol.ParseLine)
            .ToList();

    private static string TextOf(DaemonMessage m) => m switch
    {
        NonJson n => n.Text,
        AppLog l => l.Log,
        DaemonLog d => d.Message,
        _ => string.Empty,
    };

    [Fact]
    public void StartOk_YieldsAppStartThenAppStarted_SameAppId()
    {
        var messages = Parse("start-ok");

        var start = Assert.Single(messages.OfType<AppStart>());
        var started = Assert.Single(messages.OfType<AppStarted>());
        Assert.Equal(start.AppId, started.AppId);
        Assert.False(string.IsNullOrEmpty(start.AppId));
        Assert.Equal("chrome", start.DeviceId);
        Assert.True(start.SupportsRestart);
        Assert.Equal("run", start.LaunchMode);
        Assert.True(messages.IndexOf(start) < messages.IndexOf(started));
    }

    [Fact]
    public void ReloadOk_ResponseCodeZero()
    {
        var response = Assert.Single(Parse("reload-ok").OfType<Response>(), r => r.Id == 1);

        Assert.Equal(0, response.Code);
        Assert.Null(response.Error);
    }

    [Fact]
    public void ReloadCompileError_ResponseCodeNonZero_MessageMentionsError()
    {
        var response = Assert.Single(Parse("reload-compile-error").OfType<Response>(), r => r.Id == 1);

        Assert.NotEqual(0, response.Code);
        Assert.NotNull(response.Code);
        Assert.Contains("compile", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeException_HasExceptionHeaderInAppLogOrDaemonLog()
    {
        var messages = Parse("runtime-exception");

        // On web the framework error is printed as plain text (NonJson); on devices it comes through app.log.
        Assert.Contains(
            messages,
            m => TextOf(m).Contains("EXCEPTION CAUGHT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(messages, m => TextOf(m).Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildFail_ContainsNonJsonOrErrorLines_AndNoAppStarted()
    {
        var messages = Parse("build-fail");

        Assert.DoesNotContain(messages, m => m is AppStarted);
        Assert.Contains(messages, m => m is NonJson or AppStop { Error: not null });
        Assert.Contains(messages, m => m is AppStop);
    }

    [Theory]
    [InlineData("build-fail")]
    [InlineData("reload-compile-error")]
    public void CompileFailureFixtures_StderrSidecarContainsCompilerError(string fixture)
    {
        var stderr = File.ReadAllText(TestPaths.Fixture("daemon", "3.22.3", fixture + ".stderr.txt"));

        Assert.Contains("Error:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_ResponseThenAppStop()
    {
        var messages = Parse("stop");

        var response = Assert.Single(messages.OfType<Response>());
        var stop = Assert.Single(messages.OfType<AppStop>());
        Assert.Null(response.Code); // result is the bare boolean true, not an object
        Assert.Null(response.Error);
        Assert.True(messages.IndexOf(response) < messages.IndexOf(stop));
    }

    [Fact]
    public void ProgressMessages_ParseMessageAndFinished()
    {
        var progress = Parse("start-ok").OfType<AppProgress>().ToList();

        Assert.Contains(progress, p => p.Message is not null && !p.Finished);
        Assert.Contains(progress, p => p.Message is null && p.Finished);
    }

    [Fact]
    public void UnknownEvent_ReturnsOtherEvent()
    {
        var other = Assert.Single(Parse("start-ok").OfType<OtherEvent>(), e => e.Event == "app.debugPort");

        Assert.Equal("app.debugPort", other.Event);
    }

    [Fact]
    public void AppLogAndDaemonLog_ParseFields()
    {
        var log = Assert.IsType<AppLog>(Assert.Single(DaemonProtocol.ParseLine(
            "[{\"event\":\"app.log\",\"params\":{\"appId\":\"a1\",\"log\":\"hello\\n\",\"error\":true}}]")));
        var daemon = Assert.IsType<DaemonLog>(Assert.Single(DaemonProtocol.ParseLine(
            "[{\"event\":\"daemon.logMessage\",\"params\":{\"level\":\"warning\",\"message\":\"careful\"}}]")));

        Assert.Equal(new AppLog("a1", "hello\n", true), log);
        Assert.Equal(new DaemonLog("warning", "careful"), daemon);
    }

    [Fact]
    public void ErrorResponse_ParsesErrorText()
    {
        var response = Assert.IsType<Response>(Assert.Single(DaemonProtocol.ParseLine(
            "[{\"id\":4,\"error\":\"app not found\"}]")));

        Assert.Equal(new Response(4, null, null, "app not found"), response);
    }

    [Fact]
    public void MultipleObjectsInOneLine_AreAllReturned()
    {
        var messages = DaemonProtocol.ParseLine(
            "[{\"event\":\"app.started\",\"params\":{\"appId\":\"a1\"}},{\"id\":2,\"result\":{\"code\":0,\"message\":\"\"}}]");

        Assert.Equal(2, messages.Count);
        Assert.IsType<AppStarted>(messages[0]);
        Assert.IsType<Response>(messages[1]);
    }

    [Fact]
    public void NonJsonLine_ReturnsNonJson()
    {
        var messages = DaemonProtocol.ParseLine("Launching lib/main.dart on Chrome in debug mode...");

        Assert.Equal(new NonJson("Launching lib/main.dart on Chrome in debug mode..."), Assert.Single(messages));
    }

    [Fact]
    public void MalformedJsonInBrackets_ReturnsNonJson()
    {
        var messages = DaemonProtocol.ParseLine("[{\"event\":");

        Assert.Equal(new NonJson("[{\"event\":"), Assert.Single(messages));

        var closed = DaemonProtocol.ParseLine("[not json]");

        Assert.IsType<NonJson>(Assert.Single(closed));
    }

    [Fact]
    public void EncodeRestart_Format()
    {
        Assert.Equal(
            "[{\"id\":7,\"method\":\"app.restart\",\"params\":{\"appId\":\"a1\",\"fullRestart\":false,\"pause\":false,\"reason\":\"manual\"}}]",
            DaemonProtocol.EncodeRestart(7, "a1", fullRestart: false));
        Assert.Contains("\"fullRestart\":true", DaemonProtocol.EncodeRestart(8, "a1", fullRestart: true));
    }

    [Fact]
    public void EncodeStop_Format()
    {
        Assert.Equal(
            "[{\"id\":3,\"method\":\"app.stop\",\"params\":{\"appId\":\"a1\"}}]",
            DaemonProtocol.EncodeStop(3, "a1"));
    }
}
