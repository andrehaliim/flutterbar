namespace FlutterBar.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssemblyLoads()
    {
        Assert.NotNull(typeof(FlutterBar.Core.AssemblyMarker).Assembly);
    }
}
