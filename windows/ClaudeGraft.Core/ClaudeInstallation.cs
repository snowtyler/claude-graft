// The shape of this lookup, one answer for which Claude and where its profiles
// live, follows ClaudeInstallation in James's Windows port (aaditya-v-more/claude-graft#9).

namespace ClaudeGraft.Core;

/// <summary>
/// Which Claude Desktop is installed, how it is started, and where its profiles
/// live. Everything that needs one of these reads it here, so the exe and the
/// profiles never come from two different installs.
/// </summary>
public sealed record ClaudeInstallation(string Exe, string ProfilesRoot, PackagedClaude.Install? Package)
{
    /// Replaced in tests; the real one asks the disk and the package manager.
    public static Func<ClaudeInstallation?> Locate { get; set; } = Find;

    private static readonly object Gate = new();
    private static (ClaudeInstallation? found, DateTime at) _cached;

    /// Read on every path join, so held for half a minute; an install or a
    /// switch between packages is still noticed without a restart.
    public static ClaudeInstallation? Current
    {
        get
        {
            lock (Gate)
            {
                if (_cached.at == default || DateTime.UtcNow - _cached.at > TimeSpan.FromSeconds(30))
                    _cached = (Locate(), DateTime.UtcNow);
                return _cached.found;
            }
        }
    }

    public static void ForgetForTests() { lock (Gate) _cached = default; }

    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static ClaudeInstallation? Find()
    {
        if (Launcher.SquirrelExe() is { } exe) return new(exe, Roaming, null);
        return PackagedClaude.Locate() is { } package
            ? new(package.Exe, PackageProfilesRoot(package.FamilyName, Roaming, Local), package)
            : null;
    }

    /// Both packages were measured redirected, so their own folder is the answer
    /// even before Claude has made it. Only a main profile in AppData with none
    /// in the package says otherwise, and that is noted as something to look into.
    public static string PackageProfilesRoot(string family, string roaming, string local)
    {
        var redirected = Path.Combine(local, "Packages", family, "LocalCache", "Roaming");
        if (!Directory.Exists(Path.Combine(redirected, "Claude")) && Directory.Exists(Path.Combine(roaming, "Claude")))
        {
            Diagnostics.Note("paths.package-not-redirected", new Dictionary<string, object?> { ["family"] = family });
            return roaming;
        }
        return redirected;
    }
}
