using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

public class UsageStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static UsageEntry Live(TimeSpan age, UsageRefusal? refusal = null) => new()
    {
        Usage = new Usage { FiveHour = 8, Week = 33, Sampled = Now - age },
        IsLive = true,
        UpdatedAt = Now - age,
        Refusal = refusal,
    };

    [Fact(DisplayName = "a figure fetched moments ago says it was updated just now")]
    public void FreshFigure()
    {
        var line = UsageStatus.Describe(Live(TimeSpan.FromSeconds(20)), fetching: false, Now);
        Assert.Equal(new UsageStatus.Line("Usage updated just now", false), line);
    }

    [Fact(DisplayName = "an older figure says how old it is")]
    public void OlderFigure()
    {
        var line = UsageStatus.Describe(Live(TimeSpan.FromMinutes(3)), fetching: false, Now);
        Assert.Equal("Usage updated 3m ago", line!.Text);
    }

    [Fact(DisplayName = "a read going out says it is refreshing, whatever the last one found")]
    public void Refreshing()
    {
        var line = UsageStatus.Describe(Live(TimeSpan.FromMinutes(12),
            new UsageRefusal { Kind = RefusalKind.RateLimited }), fetching: true, Now);
        Assert.Equal(new UsageStatus.Line("Refreshing usage…", false), line);
    }

    [Fact(DisplayName = "a rate-limited refresh warns, names the figure's age and when it will try again")]
    public void RateLimited()
    {
        var line = UsageStatus.Describe(Live(TimeSpan.FromMinutes(12),
            new UsageRefusal { Kind = RefusalKind.RateLimited, Status = 429, RetryAt = Now.AddMinutes(4) }),
            fetching: false, Now);
        Assert.Equal(new UsageStatus.Line(
            "Anthropic is limiting usage checks right now. Showing figures from 12m ago, next try in 4m.", true), line);
    }

    [Fact(DisplayName = "a refusal with no figure behind it still says why")]
    public void RefusedWithNothingToShow()
    {
        var entry = new UsageEntry { Refusal = new UsageRefusal { Kind = RefusalKind.Failed, Status = 500 } };
        var line = UsageStatus.Describe(entry, fetching: false, Now);
        Assert.Equal(new UsageStatus.Line("Usage could not be refreshed (Anthropic answered 500).", true), line);
    }

    [Fact(DisplayName = "a figure off disk says it is Claude's own record")]
    public void DiskFigure()
    {
        var entry = new UsageEntry { IsLive = false, UpdatedAt = Now.AddHours(-2) };
        Assert.Equal("From Claude's own record, 2h 0m ago", UsageStatus.Describe(entry, false, Now)!.Text);
    }

    [Theory(DisplayName = "each way a read can fail is told apart")]
    [InlineData(429, RefusalKind.RateLimited)]
    [InlineData(401, RefusalKind.LoginRefused)]
    [InlineData(403, RefusalKind.LoginRefused)]
    [InlineData(500, RefusalKind.Failed)]
    public void KindOfStatus(int status, RefusalKind kind) =>
        Assert.Equal(kind, UsageRefusal.KindOf(new UsageApi.Failure(status, null)));

    [Fact(DisplayName = "a read that never reached Anthropic is unreachable, not refused")]
    public void Unreachable() =>
        Assert.Equal(RefusalKind.Unreachable, UsageRefusal.KindOf(new HttpRequestException("offline")));
}
