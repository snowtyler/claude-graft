namespace ClaudeGraft.Core;

/// <summary>
/// What one profile's usage is, from whichever source could answer. The live
/// endpoint is preferred — current and with exact resets — and the on-disk
/// history is the fallback for a profile whose login could not be read or whose
/// endpoint is briefly refusing.
///
/// Far simpler than the Mac's monitor: DPAPI reads the key without a dialog, so
/// there is no prompting to schedule around — just a per-profile cache to keep a
/// refresh off the endpoint and a backoff so a refused call is not retried into
/// a rate limit.
/// </summary>
public sealed record UsageEntry
{
    public Usage? Usage { get; init; }
    public bool IsLive { get; init; }
    public string? Plan { get; init; }
    public bool HasUsage => Usage is not null;
}

public static class UsageMonitor
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, (DateTimeOffset at, UsageApi.Reading reading)> LiveCache = new();
    private static readonly Dictionary<string, (DateTimeOffset until, int failures)> Backoff = new();

    private static readonly TimeSpan LiveTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] BackoffSteps =
    {
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
    };

    /// The reading for one profile: live if it can be had, otherwise disk.
    /// <paramref name="interactive"/> skips the cache and the backoff for a
    /// figure someone pressed for.
    public static async Task<UsageEntry> ReadAsync(string profile, bool interactive = false)
    {
        var disk = Graft.UsageOf(profile);
        var reading = await LiveAsync(profile, interactive).ConfigureAwait(false);
        if (reading is null)
            return new UsageEntry { Usage = disk, IsLive = false };

        return new UsageEntry
        {
            Usage = new Usage
            {
                FiveHour = reading.FiveHour,
                Week = reading.Week,
                Organization = disk?.Organization,
                Sampled = DateTimeOffset.UtcNow,
                FiveHourReset = reading.FiveHourReset,
                WeekReset = reading.WeekReset,
            },
            IsLive = true,
            Plan = reading.Plan,
        };
    }

    /// Drops the cached reading for a profile whose figure is known to have just
    /// changed — after starting a session, say.
    public static void Invalidate(string profile)
    {
        lock (Lock) LiveCache.Remove(profile);
    }

    private static async Task<UsageApi.Reading?> LiveAsync(string profile, bool interactive)
    {
        var now = DateTimeOffset.UtcNow;
        lock (Lock)
        {
            if (!interactive && LiveCache.TryGetValue(profile, out var cached) && now - cached.at < LiveTtl)
                return cached.reading;
            if (!interactive && Backoff.TryGetValue(profile, out var wait) && now < wait.until)
                return LiveCache.TryGetValue(profile, out var c) ? c.reading : null;
        }

        string token;
        try
        {
            if (ClaudeCredentials.GetToken(profile) is not ClaudeCredentials.Token t) return Cached(profile);
            token = t.Value;
        }
        catch (ClaudeCredentials.CredentialException) { return Cached(profile); }

        try
        {
            var reading = await UsageApi.FetchAsync(token).ConfigureAwait(false);
            lock (Lock)
            {
                LiveCache[profile] = (DateTimeOffset.UtcNow, reading);
                Backoff.Remove(profile);
            }
            return reading;
        }
        catch (Exception e)
        {
            var retryAfter = (e as UsageApi.Failure)?.RetryAfter;
            lock (Lock)
            {
                var failures = (Backoff.TryGetValue(profile, out var b) ? b.failures : 0) + 1;
                var delay = retryAfter is TimeSpan ra && ra > BackoffSteps[0]
                    ? ra
                    : BackoffSteps[Math.Min(failures - 1, BackoffSteps.Length - 1)];
                Backoff[profile] = (DateTimeOffset.UtcNow + delay, failures);
            }
            return Cached(profile);
        }
    }

    private static UsageApi.Reading? Cached(string profile)
    {
        lock (Lock) return LiveCache.TryGetValue(profile, out var c) ? c.reading : null;
    }
}
