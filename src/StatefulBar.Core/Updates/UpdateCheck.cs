using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StatefulBar.Core.Updates;

public enum UpdateStatus
{
    UpToDate,
    Available,

    /// <summary>The answer could not be used (not the JSON of a release); <see cref="UpdateResult.Error"/> says why.</summary>
    Failed,
}

/// <param name="Latest">The newest published release, when the answer named one.</param>
/// <param name="ReleasePage">Set when <see cref="UpdateStatus.Available"/>: the page to open in the browser.</param>
public sealed record UpdateResult(UpdateStatus Status, Version? Latest, string? ReleasePage, string? Error);

/// <summary>
/// "Check for updates" without I/O (spec §7.4): the build downloaded from GitHub asks the GitHub Releases API for the
/// newest release of this repository and tells the user when it is newer than the running version. Nothing is ever
/// downloaded or run: the user gets a toast that opens the release page. The Microsoft Store build does not check
/// (the Store updates it).
/// </summary>
public static partial class UpdateCheck
{
    public const string Repository = "andrehaliim/statefulbar";

    /// <summary>The one address a check talks to. Answers the newest release that is neither a draft nor a prerelease.</summary>
    public static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>The automatic check runs at most this often.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The answer is a few kilobytes of JSON; anything far larger is not read.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    /// <summary>GitHub rejects requests without a User-Agent. Nothing else about the user or the PC is sent.</summary>
    public static string UserAgent(Version current) => $"StatefulBar/{Three(current)}";

    /// <summary><c>v1.2.3</c> or <c>1.2.3</c>; null for anything else (four parts, suffixes, text).</summary>
    public static Version? ParseTag(string? tag)
    {
        if (tag is null || !TagPattern().IsMatch(tag))
        {
            return null;
        }
        var parts = tag.TrimStart('v').Split('.');
        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
            ? new Version(major, minor, patch)
            : null;
    }

    /// <summary>
    /// The release page of <paramref name="version"/>. Built here from a validated version and never taken from the
    /// answer, so a wrong or tampered answer cannot make the app open another address.
    /// </summary>
    public static string ReleasePageFor(Version version) => $"https://github.com/{Repository}/releases/tag/v{Three(version)}";

    /// <summary>Reads the answer of <see cref="LatestReleaseApi"/> and compares it with the running version.</summary>
    public static UpdateResult Evaluate(string json, Version current)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(current);
        Version? latest;
        bool unpublished;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
            {
                return Failed("GitHub's answer doesn't name a release");
            }
            latest = ParseTag(tag.GetString());
            unpublished = IsTrue(root, "draft") || IsTrue(root, "prerelease");
        }
        catch (JsonException)
        {
            return Failed("GitHub's answer isn't valid JSON");
        }
        if (latest is null)
        {
            return Failed("The newest release has no version number StatefulBar understands");
        }
        return !unpublished && latest > Normalize(current)
            ? new UpdateResult(UpdateStatus.Available, latest, ReleasePageFor(latest), null)
            : new UpdateResult(UpdateStatus.UpToDate, latest, null, null);
    }

    /// <summary>True when the automatic check should run: never checked, a day ago or more, or a clock that was set back.</summary>
    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is not { } last || now - last >= Interval || last > now;

    /// <summary>The automatic check tells about a version once; <paramref name="notifiedVersion"/> is the one already told.</summary>
    public static bool ShouldNotify(Version latest, string? notifiedVersion) =>
        !Version.TryParse(notifiedVersion, out var notified) || Normalize(notified) != Normalize(latest);

    /// <summary><paramref name="version"/> as <c>major.minor.patch</c>.</summary>
    public static string Three(Version version)
    {
        var v = Normalize(version);
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // Assembly versions have four parts (1.0.1.0), tags three: compare on the first three.
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static UpdateResult Failed(string error) => new(UpdateStatus.Failed, null, null, error);

    [GeneratedRegex(@"^v?\d{1,9}\.\d{1,9}\.\d{1,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
