using FlutterBar.Core.Projects;
using FlutterBar.Core.Sessions;
using FlutterBar.Core.Settings;

namespace FlutterBar.Core.Orchestration;

/// <summary>One-session rule (brief Step 7) and when a branch switch needs advice.</summary>
public static class SessionGuard
{
    /// <summary>Changing project/config/device while not Idle or Failed asks <see cref="UiText.ConfirmSwitch"/> first.</summary>
    public static bool NeedsStopConfirmation(SessionState state) => state is not (SessionState.Idle or SessionState.Failed);

    /// <summary>A session that a branch switch affects (hot restart / re-run advice).</summary>
    public static bool IsLive(SessionState state) => state is SessionState.Starting or SessionState.Running or SessionState.Reloading;
}

/// <summary>Writes selections into <see cref="AppSettings"/> through the normalized-key accessors.</summary>
public static class ProjectSelection
{
    public static void SelectProject(AppSettings settings, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.CurrentProject = PathKey.Normalize(projectRoot);
        settings.GetOrAddProject(projectRoot);
    }

    public static void SelectConfig(AppSettings settings, string projectRoot, string? configName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.GetOrAddProject(projectRoot).Config = configName;
    }

    public static void SelectDevice(AppSettings settings, string projectRoot, string? deviceId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.GetOrAddProject(projectRoot).DeviceId = deviceId;
    }

    /// <summary>After a run: LastUsed, config and device of the project (spec §4.3), and it becomes the current project.</summary>
    public static void RecordRun(AppSettings settings, string projectRoot, string configName, string deviceId, DateTimeOffset now)
    {
        SelectProject(settings, projectRoot);
        RecordRunFor(settings, projectRoot, configName, deviceId, now);
    }

    /// <summary>LastUsed, config and device of <paramref name="projectRoot"/> only; the current project is left alone.</summary>
    public static void RecordRunFor(AppSettings settings, string projectRoot, string configName, string deviceId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var state = settings.GetOrAddProject(projectRoot);
        state.LastUsed = now;
        state.Config = configName;
        state.DeviceId = deviceId;
    }
}

/// <summary>The project flyout rescans when the last scan is older than 10 minutes (spec §5.1).</summary>
public static class ScanPolicy
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    public static bool IsStale(DateTimeOffset? lastScan, DateTimeOffset now) => lastScan is not { } last || now - last > MaxAge;
}

/// <summary>Flyout search: every whitespace-separated token must occur (case-insensitive).</summary>
public static class SearchFilter
{
    public static bool Matches(string text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
