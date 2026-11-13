using StatefulBar.Core.Config;

namespace StatefulBar.Core.Tests.Config;

public sealed class ShellSplitterTests
{
    [Theory]
    [InlineData("--a=1 --b=2", new[] { "--a=1", "--b=2" })]
    [InlineData("--dart-define=APP_NAME=\"GTS Mobile Beta\" --x", new[] { "--dart-define=APP_NAME=GTS Mobile Beta", "--x" })]
    [InlineData("  ", new string[0])]
    [InlineData("", new string[0])]
    [InlineData("'single quoted' b", new[] { "single quoted", "b" })]
    [InlineData("\"say \\\"hi\\\"\" x", new[] { "say \"hi\"", "x" })]
    [InlineData("a   b\tc", new[] { "a", "b", "c" })]
    [InlineData("\"\" x", new[] { "", "x" })]
    public void Split_Cases(string input, string[] expected) =>
        Assert.Equal(expected, ShellSplitter.Split(input));
}
