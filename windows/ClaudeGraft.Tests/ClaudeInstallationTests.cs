using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class ClaudeInstallationTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly Func<ClaudeInstallation?> _locate = ClaudeInstallation.Locate;

    public void Dispose()
    {
        ClaudeInstallation.Locate = _locate;
        ClaudeInstallation.ForgetForTests();
        _temp.Dispose();
    }

    [Theory(DisplayName = "either package keeps its profiles in its own LocalCache")]
    [InlineData("Claude_pzs8sxrjxfjjc")]
    [InlineData("AnthropicPBC.Claude_fnn82j28hfe8t")]
    public void PackageProfilesAreRedirected(string family)
    {
        var roaming = _temp.Dir("Roaming");
        var local = _temp.Dir("Local");
        _temp.Dir("Local", "Packages", family, "LocalCache", "Roaming", "Claude");
        Assert.Equal(Path.Combine(local, "Packages", family, "LocalCache", "Roaming"),
            ClaudeInstallation.PackageProfilesRoot(family, roaming, local));
    }

    [Fact(DisplayName = "a package that never ran has no profiles, and Graft does not make its folder")]
    public void PackageNeverRan()
    {
        var family = PackagedClaude.FamilyNames[1];
        var roaming = _temp.Dir("Roaming");
        var local = _temp.Dir("Local");
        var root = ClaudeInstallation.PackageProfilesRoot(family, roaming, local);
        Assert.Equal(Path.Combine(local, "Packages", family, "LocalCache", "Roaming"), root);
        Assert.False(Directory.Exists(root));
    }

    [Fact(DisplayName = "a package whose profile sits in AppData and not in its own folder is read from AppData")]
    public void PackageNotRedirected()
    {
        var roaming = _temp.Dir("Roaming");
        _temp.Dir("Roaming", "Claude");
        Assert.Equal(roaming, ClaudeInstallation.PackageProfilesRoot(PackagedClaude.FamilyNames[0], roaming, _temp.Dir("Local")));
    }

    [Fact(DisplayName = "profiles are found where the installed Claude keeps them")]
    public void ProfilesFollowTheInstall()
    {
        var root = _temp.Dir("LocalCache", "Roaming");
        ClaudeInstallation.Locate = () => new ClaudeInstallation(@"C:\claude.exe", root, null);
        ClaudeInstallation.ForgetForTests();
        Assert.Equal(root, GraftPaths.ProfilesRoot);
        Assert.Equal(Path.Combine(root, "Claude"), GraftPaths.DefaultProfile);
    }

    [Fact(DisplayName = "Graft's own data stays in AppData whichever Claude is installed")]
    public void OwnDataStays()
    {
        ClaudeInstallation.Locate = () => new ClaudeInstallation(@"C:\claude.exe", _temp.Dir("LocalCache", "Roaming"), null);
        ClaudeInstallation.ForgetForTests();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeGraft"),
            GraftPaths.OwnData);
    }
}
