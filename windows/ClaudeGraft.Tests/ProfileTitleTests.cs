using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class ProfileTitleTests
{
    [Fact(DisplayName = "a window Claude calls plain Claude takes the profile's name")]
    public void PlainClaude() => Assert.Equal("Claude 2", ProfileTitle.Compose("Claude", "Claude 2"));

    [Fact(DisplayName = "a window with nothing in its title still says whose it is")]
    public void Empty() => Assert.Equal("Work", ProfileTitle.Compose("", "Work"));

    [Fact(DisplayName = "a title Claude chose keeps its words, with the profile after them")]
    public void OwnTitleKept() => Assert.Equal("Settings — Work", ProfileTitle.Compose("Settings", "Work"));
}
