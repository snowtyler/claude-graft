namespace ClaudeGraft.Core;

/// <summary>
/// What a shortcut's Claude window is called: Claude names every window the
/// same, which is what made two of them alike on the taskbar and in Alt+Tab.
/// </summary>
public static class ProfileTitle
{
    public const string Separator = " — ";

    public static string Compose(string own, string profileName)
    {
        var trimmed = own.Trim();
        return trimmed.Length == 0 || trimmed.Equals("Claude", StringComparison.OrdinalIgnoreCase)
            ? profileName
            : trimmed + Separator + profileName;
    }
}
