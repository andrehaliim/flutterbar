using FlutterBar.Core.Settings;

namespace FlutterBar.Core.Tests.Settings;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("fb-settings-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string SettingsPath => Path.Combine(_dir.FullName, "settings.json");

    [Fact]
    public void Load_Missing_ReturnsDefaults_NoWarning()
    {
        var settings = new AppSettingsStore(SettingsPath).Load(out var warning);

        Assert.Null(warning);
        Assert.Equal(1, settings.Version);
        Assert.Empty(settings.ScanRoots);
        Assert.Empty(settings.Projects);
        Assert.Equal("full", settings.Widget.Size);
        Assert.Equal("auto", settings.Widget.Position);
        Assert.False(settings.StartWithWindows);
    }

    [Fact]
    public void Load_Corrupt_BacksUpAndReturnsDefaults()
    {
        File.WriteAllText(SettingsPath, "{oops");

        var settings = new AppSettingsStore(SettingsPath).Load(out var warning);

        Assert.NotNull(warning);
        Assert.Contains("settings.json rusak", warning);
        Assert.Equal(1, settings.Version);
        Assert.False(File.Exists(SettingsPath));
        var backups = Directory.GetFiles(_dir.FullName, "settings.json.bak-*");
        Assert.Single(backups);
        Assert.Equal("{oops", File.ReadAllText(backups[0]));
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_PathKeysCaseInsensitive()
    {
        var store = new AppSettingsStore(SettingsPath);
        var settings = new AppSettings { CurrentProject = "C:\\Work\\app", StartWithWindows = true };
        settings.ScanRoots.Add("C:\\Work");
        settings.Widget.LockedX = 12.5;
        var used = new DateTimeOffset(2026, 11, 6, 10, 42, 0, TimeSpan.FromHours(7));
        settings.Projects["c:\\work\\APP"] = new ProjectState { LastUsed = used, Config = "beta", DeviceId = "chrome" };

        store.Save(settings);
        var loaded = store.Load(out var warning);

        Assert.Null(warning);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
        Assert.Equal("C:\\Work\\app", loaded.CurrentProject);
        Assert.True(loaded.StartWithWindows);
        Assert.Equal(["C:\\Work"], loaded.ScanRoots);
        Assert.Equal(12.5, loaded.Widget.LockedX);
        var state = loaded.Projects["C:\\Work\\app"];
        Assert.Equal(used, state.LastUsed);
        Assert.Equal("beta", state.Config);
        Assert.Equal("chrome", state.DeviceId);
    }

    [Fact]
    public void Save_WritesCamelCaseKeys()
    {
        new AppSettingsStore(SettingsPath).Save(new AppSettings());

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"scanRoots\"", text);
        Assert.Contains("\"startWithWindows\"", text);
    }
}
