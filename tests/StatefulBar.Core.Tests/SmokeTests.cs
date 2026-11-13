namespace StatefulBar.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssemblyLoads()
    {
        Assert.NotNull(typeof(StatefulBar.Core.AssemblyMarker).Assembly);
    }
}
