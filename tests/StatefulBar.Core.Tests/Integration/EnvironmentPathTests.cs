using FlutterBar.Core.Integration;

namespace FlutterBar.Core.Tests.Integration;

public sealed class EnvironmentPathTests
{
    private static string Expand(string s) => s.Replace("%USERPROFILE%", @"C:\Users\Andre", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Compose_MachineThenUser_Expanded()
    {
        var path = EnvironmentPath.Compose(@"C:\Windows;C:\Git\cmd", @"%USERPROFILE%\fvm;C:\flutter\bin", null, Expand);

        Assert.Equal(@"C:\Windows;C:\Git\cmd;C:\Users\Andre\fvm;C:\flutter\bin", path);
    }

    [Fact]
    public void Compose_KeepsSessionOnlyEntriesOfTheCurrentProcess()
    {
        var path = EnvironmentPath.Compose(@"C:\Windows", @"C:\new", @"C:\Windows;D:\session\tools;c:\new\", Expand);

        Assert.Equal(@"C:\Windows;C:\new;D:\session\tools", path);
    }

    [Fact]
    public void Compose_SkipsEmptyAndDuplicateSegments()
    {
        var path = EnvironmentPath.Compose(@"C:\a;;C:\b;", @" C:\A\ ;C:\c", null, Expand);

        Assert.Equal(@"C:\a;C:\b;C:\c", path);
    }

    [Fact]
    public void Compose_NothingInRegistry_KeepsCurrent()
    {
        Assert.Equal(@"C:\x;C:\y", EnvironmentPath.Compose(null, null, @"C:\x;C:\y", Expand));
    }
}
