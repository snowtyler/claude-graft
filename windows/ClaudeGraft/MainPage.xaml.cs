using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

public sealed partial class MainPage : Page
{
    public ObservableCollection<ShortcutRow> Rows { get; } = new();
    private readonly DispatcherTimer _processTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    // The window is built once and only hidden on close, so Loaded fires a single
    // time and its usage read is the only one there would ever be. A first read
    // that comes up empty — the app auto-started at login before the network was
    // up — would then stay empty for the life of the process. This poll is what
    // heals that, and keeps the figures current the way the Mac's own 30s timer
    // does; the polling budget makes a tick that already has a fresh reading free.
    private readonly DispatcherTimer _usageTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public MainPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Reload();
            _processTimer.Start();
            _usageTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _processTimer.Stop();
            _usageTimer.Stop();
        };
        _processTimer.Tick += async (_, _) => await MarkRunning(Rows.ToList());
        _usageTimer.Tick += (_, _) => RefreshUsageQuietly();
    }

    /// A background usage read for every current row, off any button and without
    /// rebuilding the list — the timer's tick and the window reappearing both run
    /// through here. It leaves Refresh Usage alone: that button reflects a read
    /// the person asked for, not one a timer took.
    private void RefreshUsageQuietly(bool interactive = false)
    {
        foreach (var row in Rows.ToList())
            _ = LoadUsage(row, interactive);
    }

    /// The window only hides on close and shows again on the next open, so its
    /// one Loaded is long past by then; this is called each time it reappears so
    /// a figure that failed to load, or has gone stale since, is fetched afresh.
    public void OnShown()
    {
        RefreshUsageQuietly(interactive: true);
        _ = MarkRunning(Rows.ToList());
    }

    private int _reloadGeneration;

    private async void Reload(bool interactive = false)
    {
        // A later reload — finishing an edit while the first load is still in
        // flight — must own the button, or the earlier one's finally re-enables
        // it while the newer readings are still coming.
        var generation = ++_reloadGeneration;
        App.Store.Load();
        Rows.Clear();
        // The main Claude leads, the way it does in the Mac dropdown.
        var rows = new List<ShortcutRow> { ShortcutRow.Main() };
        rows.AddRange(App.Store.Shortcuts.Select(ShortcutRow.ForShortcut));
        // The hint sits below the main card while there are no shortcuts yet.
        EmptyState.Visibility = App.Store.Shortcuts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Held while any row is still reaching for a figure, so Refresh Usage is
        // not offered — nor a press swallowed — until every instance has an
        // up-to-date reading in hand. LoadUsage owns its own failures, so the
        // WhenAll only completes, never throws.
        RefreshButton.IsEnabled = false;
        var loads = new List<Task>();
        foreach (var row in rows)
        {
            Rows.Add(row);
            loads.Add(LoadUsage(row, interactive));   // fills the bars in when the answer arrives
        }
        _ = MarkRunning(rows);
        try { await Task.WhenAll(loads); }
        finally { if (generation == _reloadGeneration) RefreshButton.IsEnabled = true; }
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
            // A read that threw leaves the row without a figure; un-gate Start
            // Session so it is not held on a reading that will never land.
            if (Rows.Contains(row)) row.MarkUsageUnavailable();
            Diagnostics.Note("usage.rowFailed", new Dictionary<string, object?>
            {
                ["profile"] = row.ProfileDir,
                ["error"] = e.GetType().Name + ": " + e.Message,
            });
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload(interactive: true);

    private async void Settings_Click(object sender, RoutedEventArgs e) => await OpenSettingsAsync();

    /// Every dialog goes through here first. A dialog lives in the popup layer, a
    /// sibling of the themed content rather than a child of it, so it inherits
    /// neither the theme set on the page's root nor — as it turns out — an
    /// app-level corner override: the stock template rounds its surface from an
    /// OverlayCornerRadius resolved at the dialog's own scope, which no resource
    /// higher up ever reached. So the theme is set on the dialog directly, and the
    /// corner is set both ways the SDK might read it — the resource the stock
    /// template looks up, and the CornerRadius a template-bound one would — so the
    /// modal rounds to the 8px used everywhere else and wears the chosen theme.
    private void PrepareDialog(ContentDialog dialog)
    {
        dialog.XamlRoot = XamlRoot;
        dialog.RequestedTheme = Appearance.ToElementTheme(App.Settings.Theme);
        dialog.CornerRadius = new CornerRadius(8);
        dialog.Resources["OverlayCornerRadius"] = new CornerRadius(8);
    }

    /// Opens the settings dialog and, once Done is pressed, writes the auto-start
    /// shortcut and hands the rest to the app to save and apply everywhere.
    public async Task OpenSettingsAsync()
    {
        var dialog = new SettingsDialog(App.Settings);
        PrepareDialog(dialog);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            AutoStart.Set(dialog.AutoStartEnabled);
            App.ApplySettings(dialog.Result);
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShortcutRow row } || row.Starting) return;
        row.Problem = null;
        row.Starting = true;

        var problem = await Task.Run(() => SessionStarter.StartAsync(row.ProfileDir));

        UsageMonitor.Invalidate(row.ProfileDir);
        row.Starting = false;
        row.Problem = problem;
        if (Rows.Contains(row)) await LoadUsage(row, interactive: true);
    }

    /// Persists the keep-warm choice where that profile carries it — on the
    /// shortcut for a grafted one, in the app's settings for the main Claude. The
    /// background sweep reads both the next time it runs, so nothing else has to be
    /// nudged; a start is at most a few minutes away, not this instant.
    private void KeepWarm_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ShortcutRow row } box) return;
        var on = box.IsChecked == true;
        if (row.Shortcut is Shortcut shortcut)
        {
            shortcut.KeepWarm = on;
            App.Store.Update(shortcut);
        }
        else
        {
            App.Settings.KeepMainWarm = on;
            App.Settings.Save();
        }
    }

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
        var dialog = new ProfileDialog(App.Store, existing);
        PrepareDialog(dialog);
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
            Title = "Remove profile?",
            Content = body,
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        PrepareDialog(confirm);

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        Installer.Uninstall(shortcut);
        var problem = App.Store.Delete(shortcut.Id, deletingProfile: deleteData.IsChecked == true);
        Reload();

        if (problem is not null) await Warn("The profile folder was kept", problem);
    }

    private async Task Warn(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        };
        PrepareDialog(dialog);
        await dialog.ShowAsync();
    }
}
