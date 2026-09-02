using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// <summary>
/// A plan's live limits from Anthropic's own usage endpoint, the one Claude Code
/// uses — better than the figures a profile writes to disk in every way that
/// matters: exact reset times, and current values even for a profile that is not
/// running.
/// </summary>
public static class UsageApi
{
    public const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    public sealed record Reading
    {
        public required int FiveHour { get; init; }
        public required int Week { get; init; }
        public DateTimeOffset? FiveHourReset { get; init; }
        public DateTimeOffset? WeekReset { get; init; }
        public string? Plan { get; init; }
    }

    public sealed class Failure : Exception
    {
        public int StatusCode { get; }
        public TimeSpan? RetryAfter { get; }
        public Failure(int status, TimeSpan? retryAfter) : base($"usage endpoint answered {status}")
        {
            StatusCode = status;
            RetryAfter = retryAfter;
        }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<Reading> FetchAsync(string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if ((int)response.StatusCode != 200)
        {
            TimeSpan? retry = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is DateTimeOffset d ? d - DateTimeOffset.UtcNow : null);
            throw new Failure((int)response.StatusCode, retry);
        }

        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return ReadingFrom(doc.RootElement)
            ?? throw new Failure(0, null);
    }

    /// Separated from the request so the shape of the answer can be tested.
    public static Reading? ReadingFrom(JsonElement body)
    {
        if (Window(body, "five_hour") is not { } session) return null;
        var weekly = Window(body, "seven_day");
        return new Reading
        {
            FiveHour = session.used,
            Week = weekly?.used ?? 0,
            FiveHourReset = session.resets,
            WeekReset = weekly?.resets,
            Plan = body.TryGetProperty("subscription_type", out var s) && s.ValueKind == JsonValueKind.String
                ? Capitalize(s.GetString()) : null,
        };
    }

    private static (int used, DateTimeOffset? resets)? Window(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return null;
        if (!window.TryGetProperty("utilization", out var util) || util.ValueKind != JsonValueKind.Number) return null;
        var used = (int)Math.Round(util.GetDouble());
        DateTimeOffset? resets = window.TryGetProperty("resets_at", out var r) ? ParseDate(r) : null;
        return (used, resets);
    }

    private static DateTimeOffset? ParseDate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return date;
        if (value.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.FromUnixTimeSeconds((long)value.GetDouble());
        return null;
    }

    private static string? Capitalize(string? s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
