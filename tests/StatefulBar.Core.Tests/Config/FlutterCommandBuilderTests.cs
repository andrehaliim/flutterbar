using StatefulBar.Core.Config;

namespace StatefulBar.Core.Tests.Config;

public class FlutterCommandBuilderTests
{
    [Fact]
    public void BuildRunArgs_Full()
    {
        var config = new LaunchConfig("beta", "lib/main.dart", "beta", RunMode.Debug, ["--dart-define=A=b c"]);

        var args = FlutterCommandBuilder.BuildRunArgs(config, "emulator-5554");

        Assert.Equal(
            ["run", "--machine", "-d", "emulator-5554", "-t", "lib/main.dart", "--flavor", "beta", "--debug", "--dart-define=A=b c"],
            args);
    }

    [Fact]
    public void BuildRunArgs_NoFlavor_ReleaseMode()
    {
        var config = new LaunchConfig("r", "lib/main_prod.dart", null, RunMode.Release, []);

        var args = FlutterCommandBuilder.BuildRunArgs(config, "chrome");

        Assert.Equal(["run", "--machine", "-d", "chrome", "-t", "lib/main_prod.dart", "--release"], args);
        Assert.DoesNotContain("--flavor", args);
    }
}
