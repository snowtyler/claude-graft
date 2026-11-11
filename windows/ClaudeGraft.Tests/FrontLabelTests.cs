using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class FrontLabelTests
{
    private static Usage Figures(int? five, int week) =>
        new() { FiveHour = five, Week = week, Sampled = DateTimeOffset.UtcNow };

    [Fact(DisplayName = "the tray names the Claude in front and what its account has spent")]
    public void NameAndFigures() =>
        Assert.Equal("Claude 2 — 5h 34% · week 12%", FrontLabel.Tooltip("Claude 2", Figures(34, 12)));

    [Fact(DisplayName = "an account with no five-hour limit says so rather than 0%")]
    public void NoLimit() =>
        Assert.Equal("Work — 5h no limit · week 3%", FrontLabel.Tooltip("Work", Figures(null, 3)));

    [Fact(DisplayName = "with no figure yet the tray still says which Claude is in front")]
    public void NameAlone() => Assert.Equal("Claude", FrontLabel.Tooltip("Claude", null));

    [Fact(DisplayName = "a long name is cut to what a tray tooltip can show")]
    public void Cut() => Assert.Equal(FrontLabel.MaxLength, FrontLabel.Tooltip(new string('x', 200), null).Length);
}
