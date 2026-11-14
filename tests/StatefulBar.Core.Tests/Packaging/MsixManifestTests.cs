using System.Xml.Linq;

namespace StatefulBar.Core.Tests.Packaging;

/// <summary>
/// Guards <c>packaging/msix/AppxManifest.xml</c>: the identity must stay what Partner Center issued (a different value
/// makes the Store reject the package), and the executables and the alias must match what the build produces.
/// </summary>
public sealed class MsixManifestTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap5 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
    private static readonly XNamespace Desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    private static readonly XNamespace Rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    private static readonly XElement Package =
        XDocument.Load(Path.Combine(TestPaths.RepoRoot, "packaging", "msix", "AppxManifest.xml")).Root!;

    [Fact]
    public void Identity_IsThePartnerCenterIdentity()
    {
        var identity = Package.Element(Foundation + "Identity")!;

        Assert.Equal("AndreHaliimStudio.StatefulBar", identity.Attribute("Name")!.Value);
        Assert.Equal("CN=FF5EBF5A-5CC5-4789-8A06-8A2C2E5A87CF", identity.Attribute("Publisher")!.Value);
        Assert.Equal("x64", identity.Attribute("ProcessorArchitecture")!.Value);
        Assert.Equal(
            "Andre Haliim Studio",
            Package.Element(Foundation + "Properties")!.Element(Foundation + "PublisherDisplayName")!.Value);
    }

    [Fact]
    public void Version_IsThePlaceholderThePublishScriptReplaces() =>
        Assert.Equal("0.0.0.0", Package.Element(Foundation + "Identity")!.Attribute("Version")!.Value);

    [Fact]
    public void Applications_AreTheWidgetAndTheCli()
    {
        var executables = Package.Element(Foundation + "Applications")!
            .Elements(Foundation + "Application")
            .Select(a => a.Attribute("Executable")!.Value);

        // The assembly names of StatefulBar.App and StatefulBar.Cli.
        Assert.Equal(["StatefulBar.exe", "sbar.exe"], executables);
    }

    [Fact]
    public void Cli_IsReachableAsSbar()
    {
        var alias = Assert.Single(Package.Descendants(Uap5 + "ExecutionAlias"));

        Assert.Equal("sbar.exe", alias.Attribute("Alias")!.Value);
        Assert.Equal("sbar.exe", alias.Ancestors(Uap5 + "Extension").Single().Attribute("Executable")!.Value);
    }

    [Fact]
    public void Widget_DeclaresAStartupTask()
    {
        var task = Assert.Single(Package.Descendants(Desktop + "StartupTask"));

        Assert.Equal("StatefulBar", task.Attribute("DisplayName")!.Value);
        Assert.Equal("StatefulBar.exe", task.Parent!.Attribute("Executable")!.Value);
    }

    [Fact]
    public void Package_RunsFullTrust() =>
        Assert.Contains(
            Package.Element(Foundation + "Capabilities")!.Elements(Rescap + "Capability"),
            c => c.Attribute("Name")!.Value == "runFullTrust");
}
