using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

/// One row in the manager: a profile, where it reads its chats from, the folder
/// its data lives in, and — filled in asynchronously — its plan usage.
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
        }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class MainPage : Page
{
    public ObservableCollection<ShortcutRow> Rows { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload(bool interactive = false)
    {
        App.Store.Load();
        Rows.Clear();
        // The main Claude leads, the way it does in the Mac dropdown.
        var rows = new List<ShortcutRow> { ShortcutRow.Main() };
        rows.AddRange(App.Store.Shortcuts.Select(ShortcutRow.ForShortcut));
        foreach (var row in rows)
        {
            Rows.Add(row);
            _ = LoadUsage(row, interactive);   // fills the bars in when the answer arrives
        }
        // The hint sits below the main card while there are no shortcuts yet.
        EmptyState.Visibility = App.Store.Shortcuts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadUsage(ShortcutRow row, bool interactive)
    {
        // row.ProfileDir, not row.Shortcut.ProfileDir — the main row has no
        // shortcut behind it, and dereferencing one swallowed its whole load in
        // an unobserved task, which is why the main account showed no usage.
        var entry = await UsageMonitor.ReadAsync(row.ProfileDir, interactive);
        // Back on the UI thread after the await; the row may still be shown.
        if (Rows.Contains(row)) row.SetUsage(entry);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload(interactive: true);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutRow row })
        {
            var config = row.Shortcut is Shortcut s
                ? App.Store.ConfigFor(s)
                : new GraftConfig { ProfileDir = row.ProfileDir, SourceDir = null };
            Task.Run(() => Launcher.Open(config));
        }
    }

    private async void Add_Click(object sender, RoutedEventArgs e) => await EditProfile(null);

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutRow row } && row.Shortcut is Shortcut s)
            await EditProfile(s);
    }

    private async Task EditProfile(Shortcut? existing)
    {
        var dialog = new ProfileDialog(App.Store, existing) { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            if (dialog.IsNew) App.Store.Add(dialog.Result);
            else App.Store.Update(dialog.Result);

            var config = App.Store.ConfigFor(dialog.Result);
            await Task.Run(() => Graft.Apply(config));
            Reload();
        }
        else if (result == ContentDialogResult.Secondary && existing is not null)
        {
            await ConfirmDelete(existing);
        }
    }

    private async Task ConfirmDelete(Shortcut shortcut)
    {
        var deleteData = new CheckBox { Content = "Also delete its chats and login (cannot be undone)" };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = $"“{shortcut.Name}” will be removed from the list.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(deleteData);

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove profile?",
            Content = body,
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var problem = App.Store.Delete(shortcut.Id, deletingProfile: deleteData.IsChecked == true);
        Reload();

        if (problem is not null)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "The profile folder was kept",
                Content = problem,
                CloseButtonText = "OK",
            }.ShowAsync();
        }
    }
}
