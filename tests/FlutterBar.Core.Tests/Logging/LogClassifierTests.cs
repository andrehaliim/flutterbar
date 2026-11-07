using FlutterBar.Core.Daemon;
using FlutterBar.Core.Logging;

namespace FlutterBar.Core.Tests.Logging;

public class LogClassifierTests
{
    private static List<string> FixtureTexts() =>
        File.ReadAllLines(TestPaths.Fixture("daemon", "3.22.3", "runtime-exception.jsonl"))
            .SelectMany(DaemonProtocol.ParseLine)
            .Select(m => m switch { NonJson n => n.Text, AppLog l => l.Log, _ => string.Empty })
            .ToList();

    [Fact]
    public void Classifier_HeaderAndFooter()
    {
        var texts = FixtureTexts();
        Assert.Contains(texts, LogClassifier.IsExceptionHeader);
        Assert.Contains(texts, LogClassifier.IsExceptionFooter);

        Assert.True(LogClassifier.IsExceptionHeader("════════ Exception caught by widgets library ═══════════════════"));
        Assert.True(LogClassifier.IsExceptionHeader("  ══╡ EXCEPTION CAUGHT BY RENDERING LIBRARY ╞═══"));
        Assert.False(LogClassifier.IsExceptionHeader("Exception caught by nothing"));
        Assert.False(LogClassifier.IsExceptionHeader("════════"));
        Assert.False(LogClassifier.IsExceptionHeader(""));

        Assert.True(LogClassifier.IsExceptionFooter(new string('═', 20)));
        Assert.True(LogClassifier.IsExceptionFooter(" " + new string('═', 80) + " "));
        Assert.False(LogClassifier.IsExceptionFooter(new string('═', 19)));
        Assert.False(LogClassifier.IsExceptionFooter(new string('═', 20) + "x"));
    }

    // Android: flutter forwards logcat lines with their tag/pid prefix (flutter_tools AdbLogReader); iOS: "flutter: ".
    // Synthesized from that format, not recorded (no Android fixture yet, see tests/fixtures/daemon/README.md).
    [Theory]
    [InlineData("I/flutter ( 1234): ══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══════════════════════════")]
    [InlineData("I/flutter (12345): ════════ Exception caught by rendering library ═════════════")]
    [InlineData("E/flutter (  999): ══╡ EXCEPTION CAUGHT BY FLUTTER FRAMEWORK ╞══")]
    [InlineData("I/flutter: ══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══")]
    [InlineData("flutter: ══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══════════")]
    public void Header_WithDeviceLogPrefix(string line) => Assert.True(LogClassifier.IsExceptionHeader(line));

    [Theory]
    [InlineData("I/flutter ( 1234): ════════════════════════════════════════════════════════════════════════════════")]
    [InlineData("I/flutter (12345):  ════════════════════════════════════")]
    [InlineData("flutter: ════════════════════════════════════════")]
    public void Footer_WithDeviceLogPrefix(string line) => Assert.True(LogClassifier.IsExceptionFooter(line));

    [Theory]
    [InlineData("I/flutter ( 1234): hello")]
    [InlineData("I/flutter ( 1234): Bad state: boom")]
    [InlineData("I/chromium ( 1234): ══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══")]
    [InlineData("X/flutter ( 1234): ══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══")]
    [InlineData("I/flutter ( 1234): ════════")]
    public void PrefixedNonFramingLines_AreNotHeaderOrFooter(string line)
    {
        Assert.False(LogClassifier.IsExceptionHeader(line));
        Assert.False(LogClassifier.IsExceptionFooter(line));
    }
}
