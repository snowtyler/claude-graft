using System.Runtime.CompilerServices;
using Xunit;

namespace ClaudeGraft.Tests;

public class RowTemplateAuditTests
{
    // A reused row's x:Bind sets IsChecked before Tag, so a Checked or Unchecked
    // handler in the template writes the new row's value into the old row's setting.
    [Fact(DisplayName = "a checkbox in the profile list saves only on a click, never on a binding's change")]
    public void KeepWarmSavesOnClickOnly()
    {
        var xaml = File.ReadAllText(Path.Combine(WindowsRoot(), "ClaudeGraft", "MainPage.xaml"));
        Assert.DoesNotMatch(@"\s(Un)?Checked=""", xaml);
        Assert.Contains("Click=\"KeepWarm_Click\"", xaml);
    }

    private static string WindowsRoot([CallerFilePath] string here = "") =>
        Directory.GetParent(here)!.Parent!.FullName;
}
