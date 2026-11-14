using StatefulBar.Core.Integration;

namespace StatefulBar.Core.Tests.Integration;

public sealed class PackageIdentityTests
{
    // The test host is a plain process: the Windows API must report "no package" rather than fail.
    [Fact]
    public void TestRun_IsNotPackaged() => Assert.False(PackageIdentity.IsPackaged);
}
