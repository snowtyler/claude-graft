using System.ComponentModel;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClaudeGraft;

/// One profile as the manager and the flyout both show it: where it reads its
/// chats from, the folder its data lives in, whether a Claude is holding it
/// right now, and — filled in asynchronously — its plan usage.
public sealed class ShortcutRow : INotifyPropertyChanged
{
    /// Null for the main Claude, which has no shortcut behind it.
    public Shortcut? Shortcut { get; set; }
    public string ProfileDir { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string Folder { get; set; } = "";

    /// The main account cannot be renamed or re-sourced, so its Edit is hidden.
    public bool IsEditable => Shortcut is not null;
    public Visibility EditVisibility => IsEditable ? Visibility.Visible : Visibility.Collapsed;

    public static ShortcutRow ForShortcut(Shortcut s) => new()
    {
        Shortcut = s, ProfileDir = s.ProfileDir, Name = s.Name,
        SourceLabel = App.Store.Label(s.Source), Folder = s.Folder,
    };

    public static ShortcutRow Main() => new()
    {
        ProfileDir = GraftPaths.DefaultProfile, Name = "Claude",
        SourceLabel = "Main account", Folder = "Claude",
    };

    private bool _running;

    /// A Claude is holding this profile. The dot beside the name reads it the
    /// way the Mac flyout's does — lit for the account with a window open.
    public void SetRunning(bool running)
    {
        if (running == _running) return;
        _running = running;
        Notify(nameof(StatusBrush));
    }

    // Lit green when a Claude holds the profile; a faint, theme-neutral grey
    // when none does, so the dot is legible on either background without a
    // converter to reach the theme brushes.
    public Brush StatusBrush => _running
        ? new SolidColorBrush(Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5A))
        : new SolidColorBrush(Color.FromArgb(0x66, 0x88, 0x88, 0x88));

    private UsageEntry? _usage;
    private bool _usageKnown;

    public void SetUsage(UsageEntry entry)
    {
        _usage = entry;
        _usageKnown = true;
        foreach (var name in new[]
        {
            nameof(FiveHour), nameof(Week), nameof(FiveHourText), nameof(WeekText),
            nameof(BarsVisibility), nameof(NoUsageVisibility),
        }) Notify(name);
    }

    public int FiveHour => _usage?.Usage?.FiveHour ?? 0;
    public int Week => _usage?.Usage?.Week ?? 0;

    public string FiveHourText => Line("5 hours", FiveHour, _usage?.Usage?.FiveHourReset);
    public string WeekText => Line("Week", Week, _usage?.Usage?.WeekReset);

    private static string Line(string label, int percent, DateTimeOffset? reset)
    {
        var text = $"{label} · {percent}%";
        if (reset is DateTimeOffset r && Graft.Countdown(r) is string left) text += $" · resets in {left}";
        return text;
    }

    public Visibility BarsVisibility => _usage?.HasUsage == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoUsageVisibility =>
        _usageKnown && _usage?.HasUsage != true ? Visibility.Visible : Visibility.Collapsed;

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
