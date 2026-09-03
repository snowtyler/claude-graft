using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

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
        _ = MarkRunning(rows);
        // The hint sits below the main card while there are no shortcuts yet.
        EmptyState.Visibility = App.Store.Shortcuts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// Lights each row's dot for the profile a Claude is holding, the way the
    /// flyout does. Reading what is running is a WMI query, slow enough that doing
    /// it inline would stall the list drawing, so it runs off the UI thread and
    /// sets the dots when it lands — a beat late is nothing the eye catches.
    private async Task MarkRunning(IReadOnlyList<ShortcutRow> rows)
    {
        var processes = await Task.Run(ClaudeProcesses.Enumerate);
        foreach (var row in rows)
            if (Rows.Contains(row)) row.SetRunning(ClaudeProcesses.IsRunning(row.ProfileDir, processes));
    }

    private async Task LoadUsage(ShortcutRow row, bool interactive)
    {
        // Wrapped because this runs in a fire-and-forget task: an unhandled
        // throw here has nowhere to surface and vanishes, which is exactly how
        // the main account's missing usage hid a null-dereference. A per-row
        // failure is written down rather than swallowed, and the other rows go
        // on loading.
        try
        {
            // row.ProfileDir, not row.Shortcut.ProfileDir — the main row has no
            // shortcut behind it.
            var entry = await UsageMonitor.ReadAsync(row.ProfileDir, interactive);
            // Back on the UI thread after the await; the row may still be shown.
            if (Rows.Contains(row)) row.SetUsage(entry);
        }
        catch (Exception e)
        {
            Diagnostics.Note("usage.rowFailed", new Dictionary<string, object?>
            {
                ["profile"] = row.ProfileDir,
                ["error"] = e.GetType().Name + ": " + e.Message,
            });
        }
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
            var shortcut = dialog.Result;
            var previousName = existing?.Name;
            var previousFolder = existing?.Folder;

            // A changed folder moves the profile's chats and login to the new
            // name rather than abandoning them at the old one. Its Claude must be
            // closed first — moving the files it has open could lose a chat — and
            // the move itself refuses to write over a folder already in use.
            if (existing is not null && previousFolder is not null
                && !Fs.SamePath(GraftPaths.Profile(previousFolder), GraftPaths.Profile(shortcut.Folder)))
            {
                if (ClaudeProcesses.IsRunning(GraftPaths.Profile(previousFolder)))
                {
                    await Warn("Close Claude first",
                        $"“{existing.Name}” is open. Quit its Claude window before changing the folder — "
                        + "moving a profile's files while Claude is using them could lose chats.");
                    return;
                }
                var moved = await Task.Run(() => Graft.MoveProfileFolder(previousFolder, shortcut.Folder));
                if (moved == Graft.ProfileMove.TargetExists)
                {
                    await Warn("That folder is already in use",
                        $"A folder named “{shortcut.Folder}” already exists. Pick a name that is not in use, "
                        + "so nothing there is overwritten.");
                    return;
                }
                if (moved == Graft.ProfileMove.Failed)
                {
                    await Warn("Could not move the profile",
                        "Its files could not be moved, so nothing was changed. Make sure its Claude is closed and try again.");
                    return;
                }
            }

            if (dialog.IsNew) App.Store.Add(shortcut);
            else App.Store.Update(shortcut);

            var config = App.Store.ConfigFor(shortcut);
            await Task.Run(() => Graft.Apply(config));

            try
            {
                Installer.Install(shortcut);
                // A rename leaves the old-named .lnk behind; clear it.
                if (previousName is not null && previousName != shortcut.Name)
                    Installer.Uninstall(shortcut, previousName);
                shortcut.InstalledName = shortcut.Name;
                App.Store.Update(shortcut);
            }
            catch (Installer.InstallException ex)
            {
                await Warn("Could not create the shortcut", ex.Message);
            }
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

        Installer.Uninstall(shortcut);
        var problem = App.Store.Delete(shortcut.Id, deletingProfile: deleteData.IsChecked == true);
        Reload();

        if (problem is not null) await Warn("The profile folder was kept", problem);
    }

    private async Task Warn(string title, string message) =>
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        }.ShowAsync();
}
