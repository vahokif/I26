using I76.Game.Boot;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The update check's version comparison, tag parsing and that a source build never nags.</summary>
public class UpdateCheckTests
{
    [Theory]
    [InlineData("v0.12.0", "0.11.0", true)]
    [InlineData("v0.11.0", "0.11.0 (3e3e5b5)", false)]
    [InlineData("v0.11.0", "0.11.0 (3e3e5b5-dirty)", false)]
    [InlineData("v0.10.0", "0.11.0", false)]
    [InlineData("v0.9.0", "0.10.0", false)]      // numbers, not text
    [InlineData("v0.11.1", "0.11", true)]
    [InlineData("v1.0", "0.11.0", true)]
    [InlineData("v0.12.0-rc1", "0.11.0", false)] // a pre-release is never offered
    [InlineData("v0.12.0", "0.12.0-rc1", true)]  // the release is newer than its candidate
    public void Comparison(string tag, string running, bool newer) => Assert.Equal(newer, UpdateCheck.IsNewer(tag, running));

    [Theory]
    [InlineData("dev")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("nightly")]
    [InlineData("ci-57 (3e3e5b5)")]   // the GitHub builds of master (.github/workflows/builds.yml)
    public void ASourceBuildNeitherAsksNorNags(string running)
    {
        Assert.False(UpdateCheck.HasVersion(running));
        Assert.False(UpdateCheck.IsNewer("v99.0.0", running));
    }

    [Fact]
    public void ABuildWithAVersionAsks() => Assert.True(UpdateCheck.HasVersion("0.11.0 (3e3e5b5)"));

    [Fact]
    public void UnreadableTagsAreIgnored()
    {
        Assert.False(UpdateCheck.IsNewer("latest", "0.11.0"));
        Assert.False(UpdateCheck.IsNewer("", "0.11.0"));
        Assert.False(UpdateCheck.IsNewer("v1.x", "0.11.0"));
    }

    [Fact]
    public void DisplayDropsTheV()
    {
        Assert.Equal("0.12.0", UpdateCheck.Display("v0.12.0"));
        Assert.Equal("0.11.0", UpdateCheck.Short("0.11.0 (3e3e5b5)"));
    }

    [Fact]
    public void ReadsTheReleaseAnswer()
    {
        var r = UpdateCheck.ParseRelease("""{"tag_name":"v0.12.0","html_url":"https://github.com/vahokif/I26/releases/tag/v0.12.0","draft":false,"prerelease":false,"body":"x"}""");
        Assert.Equal(("v0.12.0", "https://github.com/vahokif/I26/releases/tag/v0.12.0"), r);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"message":"API rate limit exceeded"}""")]
    [InlineData("""{"tag_name":5}""")]
    [InlineData("""{"tag_name":"nightly"}""")]
    [InlineData("""{"tag_name":"v0.12.0","prerelease":true}""")]
    [InlineData("""{"tag_name":"v0.12.0","draft":true}""")]
    public void BadAnswersAreNoRelease(string json) => Assert.Null(UpdateCheck.ParseRelease(json));

    [Fact]
    public void AReleasePageOffGitHubIsReplaced()
    {
        var r = UpdateCheck.ParseRelease("""{"tag_name":"v0.12.0","html_url":"https://evil.example/x"}""");
        Assert.StartsWith("https://github.com/", r!.Value.Url);
    }
}
