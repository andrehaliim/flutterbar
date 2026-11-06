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
}
