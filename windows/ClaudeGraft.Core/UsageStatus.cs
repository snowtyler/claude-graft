namespace ClaudeGraft.Core;

/// <summary>
/// The line under a profile's usage bars: how old the figure is, and when a
/// refresh was refused, why and until when. A refused read keeps showing the
/// last good figure, so without this line a stuck number looks like a current one.
/// </summary>
public static class UsageStatus
{
    public sealed record Line(string Text, bool IsWarning);

    public static Line? Describe(UsageEntry? entry, bool fetching, DateTimeOffset now)
    {
        if (fetching) return new Line("Refreshing usage…", false);
        if (entry is null) return null;

        var from = entry.UpdatedAt is { } at ? $"Showing figures from {Ago(at, now)}" : null;
        var next = entry.Refusal?.RetryAt is { } retry && Graft.Countdown(retry, now) is { } wait
            ? $", next try in {wait}"
            : "";

        if (entry.Refusal is { } refusal)
        {
            var why = refusal.Kind switch
            {
                RefusalKind.RateLimited => "Anthropic is limiting usage checks right now.",
                RefusalKind.LoginRefused => "This profile's login was refused. Open it once so Claude can renew it.",
                RefusalKind.NoLogin => "This profile's login could not be read, so usage cannot be checked.",
                RefusalKind.Unreachable => "Could not reach Anthropic to check usage.",
                _ => refusal.Status is int status
                    ? $"Usage could not be refreshed (Anthropic answered {status})."
                    : "Usage could not be refreshed.",
            };
            return new Line(from is null ? why : $"{why} {from}{next}.", true);
        }

        if (entry.UpdatedAt is not { } updated) return null;
        return entry.IsLive
            ? new Line($"Usage updated {Ago(updated, now)}", false)
            : new Line($"From Claude's own record, {Ago(updated, now)}", false);
    }

    public static string Ago(DateTimeOffset then, DateTimeOffset now) =>
        Graft.Countdown(now, then) is { } span && now - then >= TimeSpan.FromMinutes(1)
            ? span + " ago"
            : "just now";
}
