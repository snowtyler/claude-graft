using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// The Windows counterpart to the Mac's Sparkle-and-appcast update path. The
/// installer is published as a GitHub release, so the feed is that repository's
/// releases API and there is no separate appcast to keep in step: the release is
/// the manifest. The download comes over TLS straight from the release's own
/// asset, and nothing else is contacted — the whole of the trust here.
/// </summary>
public static class Updater
{
    /// The repository the Windows installer is released from. windows-release.yml
    /// publishes here, so this is where a machine that installed from it looks.
    public const string Repo = "snowtyler/claude-graft";

    public const string ReleasesApi = "https://api.github.com/repos/" + Repo + "/releases";

    /// A Windows release worth offering: a version, the tag it lives under, and
    /// the setup asset to fetch. Notes are the release body, shown before install.
    public sealed record Release
    {
        public required Version Version { get; init; }
        public required string Tag { get; init; }
        public required string AssetName { get; init; }
        public required string DownloadUrl { get; init; }
        public required long Size { get; init; }
        public string? Notes { get; init; }
    }

    /// The running build's version, stamped from VERSION by Directory.Build.props.
    /// An unstamped build reads 0.0.0 and so treats every real release as newer,
    /// which fails safe: it offers an update rather than hiding one.
    public static Version Current =>
        typeof(Updater).Assembly.GetName().Version is { } v ? v : new Version(0, 0, 0);

    /// The mark of a Windows installer asset, matching the OutputBaseFilename the
    /// Inno script builds — ClaudeGraft-&lt;version&gt;-x64-setup.exe. It is what
    /// keeps a Mac release in the same repository from ever being offered here:
    /// a Mac entry carries a .dmg and a .zip and no setup .exe, so it has no asset
    /// to fetch and is passed over even before its tag is read.
    public static bool IsWindowsSetupAsset(string name) =>
        name.EndsWith("-x64-setup.exe", StringComparison.OrdinalIgnoreCase);

    /// The version a Windows tag names, or null for anything that is not one.
    /// The Windows convention is win-v&lt;version&gt;; a bare v&lt;version&gt; is
    /// the Mac tag and is deliberately not accepted, so the two release lines in
    /// one repository never cross.
    public static Version? VersionFromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        const string prefix = "win-v";
        var s = tag.Trim();
        if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return Version.TryParse(s[prefix.Length..], out var v) ? v : null;
    }

    /// The newest published Windows release in the feed, or null if there is
    /// none. Drafts and pre-releases are skipped, so only a full release is ever
    /// offered. Ordered by version rather than by the order GitHub returns them,
    /// since a release edited after a later one is listed out of order.
    public static Release? LatestFrom(JsonElement releases)
    {
        if (releases.ValueKind != JsonValueKind.Array) return null;

        Release? best = null;
        foreach (var entry in releases.EnumerateArray())
        {
            if (Bool(entry, "draft") || Bool(entry, "prerelease")) continue;
            if (VersionFromTag(Str(entry, "tag_name")) is not { } version) continue;
            if (SetupAsset(entry) is not { } asset) continue;

            if (best is null || version > best.Version)
                best = new Release
                {
                    Version = version,
                    Tag = Str(entry, "tag_name")!,
                    AssetName = asset.name,
                    DownloadUrl = asset.url,
                    Size = asset.size,
                    Notes = Str(entry, "body"),
                };
        }
        return best;
    }

    /// The newest Windows release, but only when it is ahead of what is running.
    /// Kept apart from the network call so the whole decision can be tested.
    public static Release? Available(JsonElement releases, Version current) =>
        LatestFrom(releases) is { } latest && latest.Version > current ? latest : null;

    private static readonly HttpClient Http = Build();

    private static HttpClient Build()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub answers 403 to a request with no User-Agent, so the app names
        // itself. The versioned Accept header pins the response shape.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClaudeGraft", Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// Asks the feed for an update, returning one only when it is newer. Throws
    /// on a network or parse failure so the caller can back off and note it;
    /// a check that simply found nothing returns null.
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(ReleasesApi, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return Available(doc.RootElement, Current);
    }

    /// Fetches the release's installer to a temp file and returns its path. The
    /// download is checked against the size the release advertised, so a fetch
    /// cut short — a dropped connection, a proxy's error page — is caught here
    /// rather than handed to the installer as a truncated exe.
    public static async Task<string> DownloadAsync(Release release, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ClaudeGraft-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, release.AssetName);

        using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(path);
            await source.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        var got = new FileInfo(path).Length;
        if (release.Size > 0 && got != release.Size)
        {
            try { File.Delete(path); } catch { }
            throw new IOException($"downloaded {got} bytes of an expected {release.Size}");
        }
        return path;
    }

    /// Hands the installer to the shell; the Inno installer relaunches the app
    /// once it finishes. The caller quits straight after — see InstallUpdateAsync.
    public static void LaunchInstaller(string path) =>
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });

    private static (string name, string url, long size)? SetupAsset(JsonElement entry)
    {
        if (!entry.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = Str(asset, "name");
            var url = Str(asset, "browser_download_url");
            if (name is null || url is null || !IsWindowsSetupAsset(name)) continue;
            var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt64() : 0;
            return (name, url, size);
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
