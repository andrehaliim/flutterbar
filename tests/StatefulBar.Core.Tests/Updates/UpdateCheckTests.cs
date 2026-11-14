using StatefulBar.Core.Updates;

namespace StatefulBar.Core.Tests.Updates;

public sealed class UpdateCheckTests
{
    private static readonly Version Current = new(1, 0, 1, 0);

    private static string Release(string tag, bool draft = false, bool prerelease = false) =>
        $$"""
        {
          "url": "https://api.github.com/repos/andrehaliim/statefulbar/releases/1",
          "html_url": "https://example.com/not-the-page-to-open",
          "tag_name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": []
        }
        """;

    [Theory]
    [InlineData("v1.0.2", "1.0.2")]
    [InlineData("1.0.2", "1.0.2")]
    [InlineData("v10.20.30", "10.20.30")]
    public void ParseTag_ReadsThreePartVersions(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateCheck.ParseTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1.0")]
    [InlineData("v1.0.2.3")]
    [InlineData("v1.0.2-beta")]
    [InlineData("release-1.0.2")]
    [InlineData("v1.0.2 ")]
    [InlineData("v1.0.x")]
    [InlineData("v99999999999.0.0")]
    public void ParseTag_RejectsAnythingElse(string? tag) => Assert.Null(UpdateCheck.ParseTag(tag));

    [Fact]
    public void Evaluate_NewerRelease_IsAvailable_WithTheReleasePageBuiltFromTheVersion()
    {
        var result = UpdateCheck.Evaluate(Release("v1.0.2"), Current);

        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal(new Version(1, 0, 2), result.Latest);
        // Never the URL from the response: only the validated version goes into a fixed address.
        Assert.Equal("https://github.com/andrehaliim/statefulbar/releases/tag/v1.0.2", result.ReleasePage);
    }

    [Theory]
    [InlineData("v1.0.1")]
    [InlineData("v1.0.0")]
    [InlineData("v0.9.9")]
    public void Evaluate_SameOrOlderRelease_IsUpToDate(string tag)
    {
        var result = UpdateCheck.Evaluate(Release(tag), Current);

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Null(result.ReleasePage);
    }

    [Fact]
    public void Evaluate_IgnoresTheFourthVersionPartOfTheRunningApp() =>
        Assert.Equal(UpdateStatus.UpToDate, UpdateCheck.Evaluate(Release("v1.0.1"), new Version(1, 0, 1, 7)).Status);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Evaluate_DraftOrPrerelease_IsNeverOffered(bool draft, bool prerelease) =>
        Assert.Equal(UpdateStatus.UpToDate, UpdateCheck.Evaluate(Release("v9.0.0", draft, prerelease), Current).Status);

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"tag_name": 5}""")]
    [InlineData("""{"tag_name": "latest"}""")]
    [InlineData("""{"message": "API rate limit exceeded"}""")]
    public void Evaluate_UnexpectedAnswer_Fails(string json)
    {
        var result = UpdateCheck.Evaluate(json, Current);

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void IsDue_NeverChecked_OrADayAgo()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.FromHours(7));

        Assert.True(UpdateCheck.IsDue(null, now));
        Assert.True(UpdateCheck.IsDue(now - TimeSpan.FromHours(24), now));
        Assert.False(UpdateCheck.IsDue(now - TimeSpan.FromHours(23), now));
    }

    // A clock set back must not postpone the next check indefinitely.
    [Fact]
    public void IsDue_LastCheckInTheFuture_IsDue()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.True(UpdateCheck.IsDue(now + TimeSpan.FromDays(3), now));
    }

    [Fact]
    public void ShouldNotify_OncePerVersion()
    {
        var latest = new Version(1, 0, 2);

        Assert.True(UpdateCheck.ShouldNotify(latest, null));
        Assert.True(UpdateCheck.ShouldNotify(latest, "1.0.1"));
        Assert.True(UpdateCheck.ShouldNotify(latest, "garbage"));
        Assert.False(UpdateCheck.ShouldNotify(latest, "1.0.2"));
    }

    [Fact]
    public void Request_GoesToThisRepositoryOnly()
    {
        Assert.Equal("https://api.github.com/repos/andrehaliim/statefulbar/releases/latest", UpdateCheck.LatestReleaseApi.AbsoluteUri);
        Assert.Equal("StatefulBar/1.0.1", UpdateCheck.UserAgent(Current));
    }
}
