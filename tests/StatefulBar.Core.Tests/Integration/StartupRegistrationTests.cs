using StatefulBar.Core.Integration;
using StatefulBar.Core.Tests.Fakes;
using Microsoft.Win32;

namespace StatefulBar.Core.Tests.Integration;

public sealed class StartupRegistrationTests
{
    private const string Exe = @"C:\Program Files\FlutterBar\FlutterBar.App.exe";
    private readonly FakeUserRegistry _registry = new();

    [Fact]
    public void CommandFor_QuotesThePath() => Assert.Equal("\"" + Exe + "\"", StartupRegistration.CommandFor(Exe));

    [Fact]
    public void Enable_WritesQuotedPathAsString()
    {
        StartupRegistration.Enable(_registry, Exe);

        Assert.Equal(new RegistryValue("\"" + Exe + "\"", RegistryValueKind.String), _registry.Read(StartupRegistration.SubKey, "FlutterBar"));
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.SubKey);
    }

    [Fact]
    public void Read_ReflectsRegistry()
    {
        Assert.Equal(StartupState.Off, StartupRegistration.Read(_registry, Exe));

        StartupRegistration.Enable(_registry, Exe);
        Assert.Equal(StartupState.ThisApp, StartupRegistration.Read(_registry, Exe.ToUpperInvariant()));

        _registry.Seed(StartupRegistration.SubKey, StartupRegistration.ValueName, @"""D:\old\FlutterBar.App.exe""", RegistryValueKind.String);
        Assert.Equal(StartupState.OtherPath, StartupRegistration.Read(_registry, Exe));

        _registry.Seed(StartupRegistration.SubKey, StartupRegistration.ValueName, Exe, RegistryValueKind.String); // unquoted by hand
        Assert.Equal(StartupState.ThisApp, StartupRegistration.Read(_registry, Exe));
    }

    [Fact]
    public void Disable_RemovesValue()
    {
        StartupRegistration.Enable(_registry, Exe);

        StartupRegistration.Disable(_registry);

        Assert.Null(_registry.Read(StartupRegistration.SubKey, StartupRegistration.ValueName));
        Assert.Equal(StartupState.Off, StartupRegistration.Read(_registry, Exe));
    }

    [Theory]
    [InlineData("")]
    [InlineData("FlutterBar.App.exe")]
    [InlineData(@"C:\a""b.exe")]
    public void Enable_InvalidPath_Throws(string exe)
    {
        Assert.Throws<ArgumentException>(() => StartupRegistration.Enable(_registry, exe));
        Assert.Equal(0, _registry.Writes);
    }
}
