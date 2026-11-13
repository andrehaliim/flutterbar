using FlutterBar.Core.Config;
using FlutterBar.Core.Devices;
using FlutterBar.Core.Orchestration;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Settings;
using FlutterBar.Core.Tests.Fakes;

namespace FlutterBar.Core.Tests.Orchestration;

public class PresentationTests
{
    private static readonly LaunchConfig Beta = new("beta", "lib/main_beta.dart", "beta", RunMode.Debug, []);
    private static readonly LaunchConfig Prod = new("prod", "lib/main.dart", null, RunMode.Release, []);

    [Theory]
    [InlineData(SessionState.Idle, false)]
    [InlineData(SessionState.Failed, false)]
    [InlineData(SessionState.Starting, true)]
    [InlineData(SessionState.Running, true)]
    [InlineData(SessionState.Reloading, true)]
    [InlineData(SessionState.Stopping, true)]
    public void SwitchNeedsConfirmation_UnlessIdleOrFailed(SessionState state, bool expected) =>
        Assert.Equal(expected, SessionGuard.NeedsStopConfirmation(state));

    [Theory]
    [InlineData(SessionState.Idle, false)]
    [InlineData(SessionState.Failed, false)]
    [InlineData(SessionState.Stopping, false)]
    [InlineData(SessionState.Starting, true)]
    [InlineData(SessionState.Running, true)]
    [InlineData(SessionState.Reloading, true)]
    public void BranchAdvice_OnlyForLiveSessions(SessionState state, bool expected) =>
        Assert.Equal(expected, SessionGuard.IsLive(state));

    [Fact]
    public void ConfirmText_IsSpecCopy() => Assert.Equal("Stop the current session and switch?", UiText.ConfirmSwitch);

    [Fact]
    public void RecordRun_StoresUnderNormalizedKey()
    {
        var settings = new AppSettings();
        var now = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

        ProjectSelection.RecordRun(settings, @"C:\Work\gts-mobile\", "beta", "emulator-5554", now);

        Assert.True(settings.TryGetProject(@"c:\work\GTS-MOBILE", out var state));
        Assert.Equal(now, state.LastUsed);
        Assert.Equal("beta", state.Config);
        Assert.Equal("emulator-5554", state.DeviceId);
        Assert.Equal(@"C:\Work\gts-mobile", settings.CurrentProject);
        Assert.Single(settings.Projects);
    }

    [Fact]
    public void SelectConfigAndDevice_KeepOtherFields()
    {
        var settings = new AppSettings();
        ProjectSelection.SelectProject(settings, @"C:\p");
        ProjectSelection.SelectConfig(settings, @"C:\p\", "beta");
        ProjectSelection.SelectDevice(settings, @"c:\P", "chrome");

        Assert.Equal(@"C:\p", settings.CurrentProject);
        Assert.True(settings.TryGetProject(@"C:\p", out var state));
        Assert.Equal("beta", state.Config);
        Assert.Equal("chrome", state.DeviceId);
        Assert.Null(state.LastUsed);
    }

    [Fact]
    public void ScanPolicy_StaleAfterTenMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(ScanPolicy.IsStale(null, now));
        Assert.False(ScanPolicy.IsStale(now - TimeSpan.FromMinutes(9), now));
        Assert.True(ScanPolicy.IsStale(now - TimeSpan.FromMinutes(10.5), now));
    }

    [Theory]
    [InlineData("gts-mobile", "", true)]
    [InlineData("gts-mobile", null, true)]
    [InlineData("gts-mobile", "GTS", true)]
    [InlineData("gts-mobile (GTSmobile)", "mob gts", true)]
    [InlineData("feature/login", "login feat", true)]
    [InlineData("gts-mobile", "web", false)]
    public void SearchFilter_AllTokensCaseInsensitive(string text, string? query, bool expected) =>
        Assert.Equal(expected, SearchFilter.Matches(text, query));

    [Fact]
    public void ConfigChip_MissingFile()
    {
        var chip = ConfigPresentation.Chip(new LaunchConfigLoadResult(false, [], []), "beta");

        Assert.Equal("No config yet", chip.Text);
        Assert.False(chip.IsError);
        Assert.Null(chip.Selected);
    }

    [Fact]
    public void ConfigChip_Errors_RedWithLines()
    {
        var result = new LaunchConfigLoadResult(true, [], [
            new ConfigError("Invalid JSON: x", null, 4),
            new ConfigError("\"mode\" is unknown.", "configurations[0].mode", null),
        ]);

        var chip = ConfigPresentation.Chip(result, "beta");

        Assert.True(chip.IsError);
        Assert.Null(chip.Selected);
        Assert.Equal(["Line 4: Invalid JSON: x", "configurations[0].mode: \"mode\" is unknown."], chip.ErrorLines);
        Assert.Contains("Line 4", chip.ToolTip);
    }

    [Fact]
    public void ConfigChip_SelectedByName_TooltipHasProgramFlavorMode()
    {
        var chip = ConfigPresentation.Chip(new LaunchConfigLoadResult(true, [Prod, Beta], []), "beta");

        Assert.Equal("beta", chip.Text);
        Assert.Same(Beta, chip.Selected);
        Assert.False(chip.IsError);
        Assert.Equal("program: lib/main_beta.dart\nflavor: beta\nmode: debug", chip.ToolTip);
    }

    [Fact]
    public void ConfigChip_NoName_FirstConfig_NoFlavorDash()
    {
        var chip = ConfigPresentation.Chip(new LaunchConfigLoadResult(true, [Prod, Beta], []), null);

        Assert.Same(Prod, chip.Selected);
        Assert.Equal("program: lib/main.dart\nflavor: -\nmode: release", chip.ToolTip);
    }

    [Fact]
    public void ConfigChip_UnknownName_IsError()
    {
        var chip = ConfigPresentation.Chip(new LaunchConfigLoadResult(true, [Beta], []), "staging");

        Assert.True(chip.IsError);
        Assert.Equal("staging", chip.Text);
        Assert.Null(chip.Selected);
        Assert.Contains("isn't in launch.json", chip.ToolTip);
    }

    [Fact]
    public void ConfigDetail_IsCompact() =>
        Assert.Equal("lib/main_beta.dart · beta · debug", ConfigPresentation.Detail(Beta));

    [Fact]
    public void DeviceLabel_NameFromCache_ElseId()
    {
        IReadOnlyList<DeviceInfo> cached = [new("emulator-5554", "sdk gphone64", "android-x64", true, null)];

        Assert.Equal("sdk gphone64", DevicePresentation.Label("emulator-5554", cached));
        Assert.Equal("R58M", DevicePresentation.Label("R58M", cached));
        Assert.Equal(UiText.PickDevice, DevicePresentation.Label(null, cached));
    }

    [Fact]
    public void AdbLocator_PrefersAndroidHome_OnlyExistingFiles()
    {
        var env = new Dictionary<string, string?>
        {
            ["ANDROID_HOME"] = @"C:\sdk-missing",
            ["ANDROID_SDK_ROOT"] = @"C:\sdk",
            ["LOCALAPPDATA"] = @"C:\Users\a\AppData\Local",
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\sdk\platform-tools\adb.exe",
            @"C:\Users\a\AppData\Local\Android\Sdk\platform-tools\adb.exe",
        };

        var adb = AdbLocator.Find(k => env.GetValueOrDefault(k), existing.Contains, new FakePaths([]));

        Assert.Equal(@"C:\sdk\platform-tools\adb.exe", adb);
    }

    [Fact]
    public void AdbLocator_FallsBackToLocalAppDataThenPath()
    {
        var local = @"C:\L\Android\Sdk\platform-tools\adb.exe";
        Assert.Equal(local, AdbLocator.Find(k => k == "LOCALAPPDATA" ? @"C:\L" : null, p => p == local, new FakePaths([])));

        var onPath = @"C:\tools\adb.exe";
        Assert.Equal(onPath, AdbLocator.Find(_ => null, p => p == onPath, new FakePaths(new() { ["adb"] = onPath })));
        Assert.Null(AdbLocator.Find(_ => null, _ => false, new FakePaths(new() { ["adb"] = onPath })));
    }
}
