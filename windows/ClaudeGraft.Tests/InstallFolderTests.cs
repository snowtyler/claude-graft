using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class InstallFolderTests
{
    [Fact(DisplayName = "leaving the install folder leaves nothing Graft starts holding it as its working folder")]
    public void LeavesTheInstallFolder()
    {
        var was = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = AppContext.BaseDirectory;
            InstallFolder.Leave();
            Assert.False(Fs.SamePath(Environment.CurrentDirectory, AppContext.BaseDirectory));
            Assert.False(Environment.CurrentDirectory.StartsWith(AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
        finally { Environment.CurrentDirectory = was; }
    }
}
