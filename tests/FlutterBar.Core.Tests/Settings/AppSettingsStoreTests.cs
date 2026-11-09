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
        Assert.Contains("settings.json is corrupt", warning);
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
        settings.Widget.OffsetFromRight = 500;
        var used = new DateTimeOffset(2026, 11, 6, 10, 42, 0, TimeSpan.FromHours(7));
        settings.Projects["c:\\work\\APP"] = new ProjectState { LastUsed = used, Config = "beta", DeviceId = "chrome" };

        store.Save(settings);
        var loaded = store.Load(out var warning);

        Assert.Null(warning);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
        Assert.Equal("C:\\Work\\app", loaded.CurrentProject);
        Assert.True(loaded.StartWithWindows);
        Assert.Equal(["C:\\Work"], loaded.ScanRoots);
        Assert.Equal(500, loaded.Widget.OffsetFromRight);
        var state = loaded.Projects["C:\\Work\\app"];
        Assert.Equal(used, state.LastUsed);
        Assert.Equal("beta", state.Config);
        Assert.Equal("chrome", state.DeviceId);
    }

    [Fact]
    public void Projects_TrailingSeparatorLookup_Hits()
    {
        var settings = new AppSettings
        {
            Projects = new Dictionary<string, ProjectState> { ["C:\\Work\\app"] = new() { Config = "x" } },
        };

        Assert.True(settings.TryGetProject("c:\\work\\APP\\", out var state));
        Assert.Equal("x", state.Config);
        Assert.False(settings.TryGetProject("C:\\Work\\other", out _));
    }

    [Fact]
    public void GetOrAddProject_Normalizes()
    {
        var settings = new AppSettings();

        var a = settings.GetOrAddProject("C:\\Work\\app\\");
        var b = settings.GetOrAddProject("c:\\work\\APP");

        Assert.Same(a, b);
        Assert.Equal(["C:\\Work\\app"], settings.Projects.Keys);
    }

    [Fact]
    public void Load_DuplicateProjectKeys_MergedWithoutException()
    {
        File.WriteAllText(SettingsPath,
            "{\"projects\":{\"C:\\\\a\\\\app\":{\"config\":\"first\"},\"c:\\\\A\\\\APP\\\\\":{\"config\":\"last\"}}}");

        var settings = new AppSettingsStore(SettingsPath).Load(out var warning);

        Assert.Null(warning);
        Assert.Single(settings.Projects);
        Assert.Equal("last", settings.Projects.Values.Single().Config);
    }

    [Fact]
    public void Load_InvalidPathKey_BacksUpAndReturnsDefaults()
    {
        File.WriteAllText(SettingsPath, "{\"projects\":{\"C:\\\\a\\u0000b\":{\"config\":\"x\"}}}");

        var settings = new AppSettingsStore(SettingsPath).Load(out var warning);

        Assert.NotNull(warning);
        Assert.Empty(settings.Projects);
        Assert.False(File.Exists(SettingsPath));
        Assert.Single(Directory.GetFiles(_dir.FullName, "settings.json.bak-*"));
    }

    [Fact]
    public void Save_WritesCamelCaseKeys()
    {
        new AppSettingsStore(SettingsPath).Save(new AppSettings());

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"scanRoots\"", text);
        Assert.Contains("\"startWithWindows\"", text);
    }

    [Fact]
    public void Save_WritesWidgetOffsetFromRightKey()
    {
        var settings = new AppSettings();
        settings.Widget.OffsetFromRight = 500;

        new AppSettingsStore(SettingsPath).Save(settings);

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"offsetFromRight\": 500", text);
        Assert.DoesNotContain("lockedX", text);
    }

    [Fact]
    public void BackgroundOpacity_DefaultsToOpaque_AndRoundTrips()
    {
        Assert.Equal(100, new AppSettings().Widget.BackgroundOpacity);
        var settings = new AppSettings();
        settings.Widget.BackgroundOpacity = 60;
        var store = new AppSettingsStore(SettingsPath);

        store.Save(settings);
        var loaded = store.Load(out var warning);

        Assert.Null(warning);
        Assert.Contains("\"backgroundOpacity\": 60", File.ReadAllText(SettingsPath));
        Assert.Equal(60, loaded.Widget.BackgroundOpacity);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("-20", 0)]
    [InlineData("250", 100)]
    [InlineData("42.6", 43)]
    [InlineData("\"55\"", 55)]
    [InlineData("\"half\"", 100)]
    [InlineData("null", 100)]
    [InlineData("{}", 100)]
    public void Load_HandEditedBackgroundOpacity_IsClampedOrOpaque(string value, int expected)
    {
        File.WriteAllText(SettingsPath, "{\"widget\":{\"size\":\"compact\",\"backgroundOpacity\":" + value + "}}");

        var settings = new AppSettingsStore(SettingsPath).Load(out var warning);

        Assert.Null(warning);
        Assert.Equal(expected, settings.Widget.BackgroundOpacity);
        Assert.Equal("compact", settings.Widget.Size); // the rest of the widget block survives
    }

    [Fact]
    public void Theme_DefaultsToSystem_AndRoundTrips()
    {
        Assert.Equal("system", new AppSettings().Widget.Theme);
        var settings = new AppSettings();
        settings.Widget.Theme = "Dark";
        var store = new AppSettingsStore(SettingsPath);

        store.Save(settings);
        var loaded = store.Load(out var warning);

        Assert.Null(warning);
        Assert.Contains("\"theme\": \"dark\"", File.ReadAllText(SettingsPath));
        Assert.Equal("dark", loaded.Widget.Theme);
    }

    [Theory]
    [InlineData("\"\\\\\\\\.\\\\DISPLAY2\"", @"\\.\DISPLAY2")]
    [InlineData("\"  \"", null)]
    [InlineData("null", null)]
    [InlineData("2", null)]
    [InlineData("{\"a\":1}", null)]
    public void Load_Monitor_IsKeptOrNull(string value, string? expected)
    {
        File.WriteAllText(SettingsPath, "{\"widget\":{\"size\":\"compact\",\"monitor\":" + value + "}}");

        var settings = new AppSettingsStore(SettingsPath).Load(out var warning);

        Assert.Null(warning);
        Assert.Equal(expected, settings.Widget.Monitor);
        Assert.Equal("compact", settings.Widget.Size);
    }

    [Fact]
    public void Monitor_DefaultsToMain_AndRoundTrips()
    {
        Assert.Null(new AppSettings().Widget.Monitor);
        var settings = new AppSettings();
        settings.Widget.Monitor = @" \\.\DISPLAY2 ";
        var store = new AppSettingsStore(SettingsPath);

        store.Save(settings);

        Assert.Equal(@"\\.\DISPLAY2", store.Load(out _).Widget.Monitor);
    }

    [Theory]
    [InlineData("\"light\"", "light")]
    [InlineData("\" LIGHT \"", "light")]
    [InlineData("\"sepia\"", "system")]
    [InlineData("1", "system")]
    [InlineData("null", "system")]
    [InlineData("[\"dark\"]", "system")]
    public void Load_HandEditedTheme_IsNormalized(string value, string expected)
    {
        File.WriteAllText(SettingsPath, "{\"widget\":{\"size\":\"compact\",\"theme\":" + value + "}}");

        var settings = new AppSettingsStore(SettingsPath).Load(out var warning);

        Assert.Null(warning);
        Assert.Equal(expected, settings.Widget.Theme);
        Assert.Equal("compact", settings.Widget.Size);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("100000")]
    public void Load_AbsurdOffsetFromRight_ResetsToAuto(string value)
    {
        File.WriteAllText(SettingsPath, "{\"widget\":{\"position\":\"manual\",\"offsetFromRight\":" + value + "}}");

        var settings = new AppSettingsStore(SettingsPath).Load(out var warning);

        Assert.Null(warning);
        Assert.Null(settings.Widget.OffsetFromRight);
    }

    [Theory]
    [InlineData("420")]
    [InlineData("0")]
    [InlineData("50000")]
    public void Load_StoredWidthOverride_IsDropped_AndNotWrittenBack(string value)
    {
        File.WriteAllText(SettingsPath, "{\"widget\":{\"size\":\"compact\",\"offsetFromRight\":300,\"widthOverride\":" + value + "}}");
        var store = new AppSettingsStore(SettingsPath);

        var settings = store.Load(out var warning);
        store.Save(settings);

        Assert.Null(warning);
        Assert.Equal("compact", settings.Widget.Size);
        Assert.Equal(300, settings.Widget.OffsetFromRight);
        Assert.DoesNotContain("widthOverride", File.ReadAllText(SettingsPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ValidOffsetFromRight_Kept()
    {
        File.WriteAllText(SettingsPath, "{\"widget\":{\"position\":\"manual\",\"offsetFromRight\":500}}");

        Assert.Equal(500, new AppSettingsStore(SettingsPath).Load(out _).Widget.OffsetFromRight);
    }

    [Theory]
    [InlineData("500.5", 501)]
    [InlineData("499.4", 499)]
    [InlineData("\"500\"", 500)]
    public void Load_NonIntegerOffsetFromRight_RoundedWithoutResettingSettings(string value, int expected)
    {
        File.WriteAllText(SettingsPath, "{\"currentProject\":\"C:/Work/app\",\"widget\":{\"position\":\"manual\",\"offsetFromRight\":" + value + "}}");

        var settings = new AppSettingsStore(SettingsPath).Load(out var warning);

        Assert.Null(warning);
        Assert.Equal(expected, settings.Widget.OffsetFromRight);
        Assert.Equal("C:/Work/app", settings.CurrentProject);
        Assert.True(File.Exists(SettingsPath)); // not treated as corrupt
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("1e300")]
    public void Load_GarbageOffsetFromRight_AutoWithoutResettingSettings(string value)
    {
        File.WriteAllText(SettingsPath, "{\"startWithWindows\":true,\"widget\":{\"offsetFromRight\":" + value + "}}");

        var settings = new AppSettingsStore(SettingsPath).Load(out var warning);

        Assert.Null(warning);
        Assert.Null(settings.Widget.OffsetFromRight);
        Assert.True(settings.StartWithWindows);
    }
}
