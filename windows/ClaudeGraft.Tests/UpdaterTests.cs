using System.Text.Json;
using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class UpdaterTests
{
    private static JsonElement Feed(params string[] entries) =>
        JsonDocument.Parse("[" + string.Join(",", entries) + "]").RootElement;

    /// A release entry the way GitHub returns one, with a Windows setup asset.
    private static string Release(string tag, bool prerelease = false, bool draft = false,
        long size = 1000, string asset = "ClaudeGraft-x-x64-setup.exe", string? body = "notes") =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "prerelease": {{(prerelease ? "true" : "false")}},
          "draft": {{(draft ? "true" : "false")}},
          "body": {{(body is null ? "null" : $"\"{body}\"")}},
          "assets": [{ "name": "{{asset}}", "browser_download_url": "https://example/{{asset}}", "size": {{size}} }]
        }
        """;

    [Fact(DisplayName = "a win-v tag names its version, and a bare Mac v tag names none")]
    public void ReadsWindowsTags()
    {
        Assert.Equal(new Version(1, 2, 3), Updater.VersionFromTag("win-v1.2.3"));
        Assert.Null(Updater.VersionFromTag("v1.2.3"));
        Assert.Null(Updater.VersionFromTag("nightly"));
        Assert.Null(Updater.VersionFromTag(null));
    }

    [Fact(DisplayName = "only the setup exe counts as a Windows asset")]
    public void RecognisesTheSetupAsset()
    {
        Assert.True(Updater.IsWindowsSetupAsset("ClaudeGraft-1.1.0-x64-setup.exe"));
        Assert.False(Updater.IsWindowsSetupAsset("ClaudeGraft-1.1.0.dmg"));
        Assert.False(Updater.IsWindowsSetupAsset("ClaudeGraft-1.1.0.zip"));
    }

    [Fact(DisplayName = "the newest full Windows release is the one offered, whatever order the feed lists them in")]
    public void PicksNewestByVersion()
    {
        var latest = Updater.LatestFrom(Feed(
            Release("win-v1.1.0"),
            Release("win-v1.3.0"),
            Release("win-v1.2.0")));

        Assert.NotNull(latest);
        Assert.Equal(new Version(1, 3, 0), latest!.Version);
        Assert.Equal("https://example/ClaudeGraft-x-x64-setup.exe", latest.DownloadUrl);
        Assert.Equal("notes", latest.Notes);
    }

    [Fact(DisplayName = "a pre-release, a draft, a Mac tag, and a release with no setup asset are all skipped")]
    public void SkipsWhatShouldNotBeOffered()
    {
        var latest = Updater.LatestFrom(Feed(
            Release("win-v2.0.0", prerelease: true),
            Release("win-v1.9.0", draft: true),
            Release("v1.8.0"),
            Release("win-v1.7.0", asset: "ClaudeGraft-1.7.0.dmg"),
            Release("win-v1.1.0")));

        Assert.NotNull(latest);
        Assert.Equal(new Version(1, 1, 0), latest!.Version);
    }

    [Fact(DisplayName = "an update is offered only when the newest release is ahead of what is running")]
    public void OffersOnlyWhatIsNewer()
    {
        var feed = Feed(Release("win-v1.1.0"));
        Assert.NotNull(Updater.Available(feed, new Version(1, 0, 0)));
        Assert.Null(Updater.Available(feed, new Version(1, 1, 0)));
        Assert.Null(Updater.Available(feed, new Version(1, 2, 0)));
    }

    [Fact(DisplayName = "a feed with nothing installable in it offers nothing rather than throwing")]
    public void EmptyFeedIsNoUpdate()
    {
        Assert.Null(Updater.LatestFrom(Feed()));
        Assert.Null(Updater.LatestFrom(JsonDocument.Parse("{}").RootElement));
    }
}
