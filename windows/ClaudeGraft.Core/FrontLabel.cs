namespace ClaudeGraft.Core;

/// <summary>
/// The tray's words for the Claude in front: its name, and the figures its
/// account is spending, short enough for a notification-area tooltip.
/// </summary>
public static class FrontLabel
{
    // Windows cuts a tray tooltip off past 127 characters.
    public const int MaxLength = 127;

    public static string Tooltip(string name, Usage? usage)
    {
        var text = usage is null
            ? name
            : name + " — " + (usage.FiveHour is int five ? $"5h {five}%" : "5h no limit") + $" · week {usage.Week}%";
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}
