using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class PackagedClaudeTests
{
    private const string Root = @"C:\Program Files\WindowsApps\Claude_2.2553.13.0_x64__pzs8sxrjxfjjc";

    private const string Manifest = """
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Applications>
            <Application Id="Claude" Executable="app\claude.exe" EntryPoint="Windows.FullTrustApplication" />
          </Applications>
        </Package>
        """;

    [Fact(DisplayName = "the manifest's application gives the exe and the id activation wants")]
    public void FromManifest()
    {
        var install = PackagedClaude.FromManifest(Root, Manifest)!;
        Assert.Equal(Root + @"\app\claude.exe", install.Exe);
        Assert.Equal("Claude_pzs8sxrjxfjjc!Claude", install.AppUserModelId);
    }

    [Fact(DisplayName = "a manifest that will not parse finds no install")]
    public void BrokenManifest() => Assert.Null(PackagedClaude.FromManifest(Root, "<Package"));

    [Fact(DisplayName = "the packaged app counts as Claude Desktop, on its own profile or the main one")]
    public void PackagedProcess()
    {
        var exe = Root + @"\app\claude.exe";
        Assert.True(ClaudeProcesses.IsDefaultInstance(exe));
        Assert.True(ClaudeProcesses.IsClaudeDesktop(exe + @" --user-data-dir=C:\Users\T\AppData\Roaming\Claude-2"));
    }

    [Fact(DisplayName = "a copy of Claude Code inside a profile is still not the desktop app")]
    public void BridgedCli() =>
        Assert.False(ClaudeProcesses.IsClaudeDesktop(@"C:\Users\T\AppData\Roaming\Claude\claude-code\2.1\claude.exe"));
}
