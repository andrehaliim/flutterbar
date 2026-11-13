using StatefulBar.Core.Integration;
using StatefulBar.Core.Tests.Fakes;
using Microsoft.Win32;

namespace StatefulBar.Core.Tests.Integration;

public sealed class UserPathEditorTests
{
    private const string Folder = @"C:\Tools\FlutterBar";
    private readonly FakeUserRegistry _registry = new();

    private static string Expand(string s) => s.Replace("%USERPROFILE%", @"C:\Users\Andre", StringComparison.OrdinalIgnoreCase);

    private RegistryValue? Path => _registry.Read(UserPathEditor.SubKey, UserPathEditor.ValueName);

    [Theory]
    [InlineData(@"C:\a;C:\Tools\FlutterBar")]
    [InlineData(@"C:\a;c:\tools\flutterbar")]
    [InlineData(@"C:\a;C:\Tools\FlutterBar\")]
    [InlineData(@"C:\a; C:\Tools\FlutterBar ;C:\b")]
    [InlineData(@"""C:\Tools\FlutterBar"";C:\a")]
    [InlineData(@"C:\a;;C:\Tools\FlutterBar;")]
    public void Contains_MatchesCaseQuotesAndTrailingSeparator(string path) => Assert.True(UserPathEditor.Contains(path, Folder, Expand));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Tools")]
    [InlineData(@"C:\Tools\FlutterBar2;C:\Tools\Flutter")]
    public void Contains_OtherFolders_False(string? path) => Assert.False(UserPathEditor.Contains(path, Folder, Expand));

    [Fact]
    public void Contains_UnexpandedVariable_ComparedRawAndExpanded()
    {
        Assert.True(UserPathEditor.Contains(@"%USERPROFILE%\bin", @"%userprofile%\bin\", Expand));
        Assert.True(UserPathEditor.Contains(@"%USERPROFILE%\bin", @"C:\Users\Andre\bin", Expand));
    }

    [Theory]
    [InlineData(null, @"C:\Tools\FlutterBar")]
    [InlineData("", @"C:\Tools\FlutterBar")]
    [InlineData(@"C:\a", @"C:\a;C:\Tools\FlutterBar")]
    [InlineData(@"C:\a;", @"C:\a;C:\Tools\FlutterBar")]
    [InlineData(@"C:\b;;C:\a", @"C:\b;;C:\a;C:\Tools\FlutterBar")]
    [InlineData(@"%USERPROFILE%\bin;C:\a", @"%USERPROFILE%\bin;C:\a;C:\Tools\FlutterBar")]
    public void Append_KeepsExistingTextVerbatim(string? path, string expected) => Assert.Equal(expected, UserPathEditor.Append(path, Folder));

    [Fact]
    public void AddFolder_Missing_CreatesExpandString()
    {
        Assert.Equal(PathAddOutcome.Added, UserPathEditor.AddFolder(_registry, Folder, Expand));
        Assert.Equal(new RegistryValue(Folder, RegistryValueKind.ExpandString), Path);
    }

    [Fact]
    public void AddFolder_ExpandString_AppendsAndKeepsKind()
    {
        _registry.Seed(UserPathEditor.SubKey, UserPathEditor.ValueName, @"%USERPROFILE%\bin;C:\x", RegistryValueKind.ExpandString);

        Assert.Equal(PathAddOutcome.Added, UserPathEditor.AddFolder(_registry, Folder, Expand));

        Assert.Equal(new RegistryValue(@"%USERPROFILE%\bin;C:\x;C:\Tools\FlutterBar", RegistryValueKind.ExpandString), Path);
    }

    [Fact]
    public void AddFolder_PlainString_KeepsPlainString()
    {
        _registry.Seed(UserPathEditor.SubKey, UserPathEditor.ValueName, @"C:\x", RegistryValueKind.String);

        UserPathEditor.AddFolder(_registry, Folder, Expand);

        Assert.Equal(new RegistryValue(@"C:\x;C:\Tools\FlutterBar", RegistryValueKind.String), Path);
    }

    [Fact]
    public void AddFolder_AlreadyPresent_DoesNotWrite()
    {
        _registry.Seed(UserPathEditor.SubKey, UserPathEditor.ValueName, @"C:\x;c:\tools\flutterbar\", RegistryValueKind.ExpandString);

        Assert.Equal(PathAddOutcome.AlreadyPresent, UserPathEditor.AddFolder(_registry, Folder, Expand));

        Assert.Equal(0, _registry.Writes);
    }

    [Fact]
    public void AddFolder_UnexpectedKind_Throws_AndDoesNotWrite()
    {
        _registry.Seed(UserPathEditor.SubKey, UserPathEditor.ValueName, "1", RegistryValueKind.DWord);

        Assert.Throws<InvalidDataException>(() => UserPathEditor.AddFolder(_registry, Folder, Expand));

        Assert.Equal(0, _registry.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"relative\dir")]
    [InlineData(@"C:\a;b")]
    [InlineData(@"C:\a""b")]
    public void AddFolder_InvalidFolder_Throws(string folder)
    {
        Assert.Throws<ArgumentException>(() => UserPathEditor.AddFolder(_registry, folder, Expand));
        Assert.Equal(0, _registry.Writes);
    }

    [Fact]
    public void IsPresent_ReadsRegistry()
    {
        Assert.False(UserPathEditor.IsPresent(_registry, Folder, Expand));
        _registry.Seed(UserPathEditor.SubKey, UserPathEditor.ValueName, Folder, RegistryValueKind.ExpandString);
        Assert.True(UserPathEditor.IsPresent(_registry, Folder, Expand));
    }
}
