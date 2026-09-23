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
        // The logo is a scale-qualified asset that ships only inside resources.pri,
        // so an unpackaged install has no ms-appx entry for it; the build copies a
        // plain-named file out beside the binary and it is loaded from there, the
        // same loose-file spot the window's own icon comes from.
        var logo = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "Logo.png");
        if (System.IO.File.Exists(logo))
            LogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(logo));
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
        _processTimer.Tick += async (_, _) =>
        {
            // Picks up an install or sign-in finished while the prompt was up.
            if (Onboarding.Check(App.Store) != _setup) { Reload(); return; }
            foreach (var row in Rows.ToList())
            {
                var was = row.SignedIn;
                row.SetSignedIn(Onboarding.IsSignedIn(row.ProfileDir));
                if (!was && row.SignedIn) _ = LoadUsage(row, interactive: true);
            }
            await MarkRunning(Rows.ToList());
        };
        _usageTimer.Tick += (_, _) => RefreshUsageQuietly();
        Setup.RecheckRequested += () => Reload();
        UsageMonitor.FetchingChanged += (profile, fetching) => DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var row in Rows.Where(r => Fs.SamePath(r.ProfileDir, profile)))
                row.SetFetching(fetching);
        });
    }

    private SetupState _setup = SetupState.Ready;

    /// Until there is a signed-in Claude there is nothing a card could show, so
    /// the list and the actions that act on it step aside for the prompt.
    private bool ShowSetup(SetupState state)
    {
        _setup = state;
        var ready = state == SetupState.Ready;
        Setup.Show(state);
        ProfileList.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        return ready;
    }

    /// A background usage read for every current row, off any button and without
    /// rebuilding the list — the timer's tick and the window reappearing both run
    /// through here. It leaves Refresh Usage alone: that button reflects a read
    /// the person asked for, not one a timer took.
    private void RefreshUsageQuietly(bool interactive = false)
    {
        if (_setup != SetupState.Ready) return;
        foreach (var row in Rows.ToList())
            _ = LoadUsage(row, interactive);
    }

    /// The window only hides on close and shows again on the next open, so its
    /// one Loaded is long past by then; this is called each time it reappears so
    /// a figure that failed to load, or has gone stale since, is fetched afresh.
    public void OnShown()
    {
        if (Onboarding.Check(App.Store) != _setup) { Reload(); return; }
        RefreshUsageQuietly(interactive: true);
        _ = MarkRunning(Rows.ToList());
        _ = MarkChatsElsewhere(Rows.ToList());
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
        if (!ShowSetup(Onboarding.Check(App.Store))) return;
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
        _ = MarkChatsElsewhere(rows);
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
    ///
    /// Click, never Checked/Unchecked: a reused row's binding sets IsChecked before
    /// Tag, so those fired for the row it used to hold and unticked it on a reload.
    private void KeepWarm_Click(object sender, RoutedEventArgs e)
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

    /// Shortcuts asked about their elsewhere-chats already, for this run only. The
    /// dialog's checkbox is the permanent silence; this just stops a second Open
    /// press re-asking.
    private readonly HashSet<Guid> _askedAboutChats = new();

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShortcutRow row }) return;

        // Before opening, never after: Claude builds its sidebar at launch, so the
        // copy has to happen first to land in the window about to open.
        if (row.Shortcut is { } s && row.Elsewhere is { } found
            && s.StopAskingChatsFor != found.Account && !_askedAboutChats.Contains(s.Id))
        {
            if (!await AskAboutChats(row, s, found)) return;
        }
        OpenRow(row);
    }

    private static void OpenRow(ShortcutRow row) => ProfileRows.Open(row);


    /// The offer at the door. Four choices folded into a dialog's three buttons
    /// and a checkbox: copy or merge them across, open without them, or cancel,
    /// with "don't ask again" alongside — the persistent silence the Mac gives a
    /// button of its own. Returns whether to go on and open the window.
    private async Task<bool> AskAboutChats(ShortcutRow row, Shortcut s, Graft.ChatsElsewhere found)
    {
        var dontAsk = new CheckBox { Content = "Don't ask again for this account" };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = row.ElsewhereNote
                   + "\n\nClaude builds its sidebar as it starts, so bringing them over now is what puts "
                   + "them in the window about to open. Every Claude has to be closed for that.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(dontAsk);

        var dialog = new ContentDialog
        {
            Title = found.Merging
                ? "Merge this account's other chats in first?"
                : "Bring this account's chats across first?",
            Content = body,
            PrimaryButtonText = row.AdoptLabel,
            SecondaryButtonText = "Open Without Them",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        PrepareDialog(dialog);
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.None) return false;   // Cancel: leave it, do not open

        if (dontAsk.IsChecked == true)
        {
            s.StopAskingChatsFor = found.Account;
            App.Store.Update(s);
        }

        if (result == ContentDialogResult.Secondary)   // Open Without Them
        {
            if (dontAsk.IsChecked != true) _askedAboutChats.Add(s.Id);
            return true;
        }

        // Copy / Merge Them Here. Open only once something arrived and nothing is
        // running: a copy refused for an open Claude leaves the "quit X" note that
        // opening a window would bury, and stays askable next time.
        var adoption = await Adopt(row, found);
        return adoption.Running.Count == 0 && adoption.Copied > 0;
    }

    private async void Adopt_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutRow row } && row.Elsewhere is { } found)
            await Adopt(row, found);
    }

    /// Off the UI thread, because it copies files and asks what is running. The
    /// result note stays on the row afterward, since Claude may not be open to
    /// show the answer for itself.
    private async Task<Graft.Adoption> Adopt(ShortcutRow row, Graft.ChatsElsewhere found)
    {
        var profile = row.ProfileDir;
        var result = await Task.Run(() => Graft.AdoptChats(found.Profile, profile, found.Account));
        if (!Rows.Contains(row)) return result;

        row.CopiedNote = result.Running.Count > 0
            ? "Nothing copied — quit " + NamesOf(result.Running) + " first"
            : result.Copied == 0
                ? "Nothing was copied"
                : $"{ShortcutRow.Chats(result.Copied)} copied from {App.Store.NameOfProfile(found.Profile)}";

        // Once they are here the offer stops being made, and after a partial copy
        // the count drops to whatever is still missing.
        await MarkChatsElsewhere(new[] { row });
        return result;
    }

    /// A list of profiles as a sentence: "Claude", "Claude and Claude-Work",
    /// "Claude, Claude-Work and Claude-Play".
    private static string NamesOf(IReadOnlyList<string> profiles)
    {
        var names = profiles.Select(App.Store.NameOfProfile).ToList();
        return names.Count switch
        {
            0 => "",
            1 => names[0],
            2 => names[0] + " and " + names[1],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    /// Fills each eligible row's "chats found elsewhere" offer, off the UI thread
    /// because it walks every profile's stores. Only a shortcut keeping its own
    /// chats is asked: the main row holds no shortcut, and a grafted one is having
    /// its sidebar filled from a source already.
    private async Task MarkChatsElsewhere(IReadOnlyList<ShortcutRow> rows)
    {
        var offers = await Task.Run(() =>
        {
            var profiles = Graft.SessionStoreProfiles();
            var map = new Dictionary<ShortcutRow, (Graft.ChatsElsewhere? offer, string? status)>();
            foreach (var row in rows)
                map[row] = (
                    row.Shortcut is { Source.Kind: SourceKind.Own } s
                        ? Graft.FindChatsElsewhere(s.ProfileDir, profiles)
                        : null,
                    SidebarSync.Status(row.ProfileDir));
            return map;
        });
        foreach (var (row, value) in offers)
            if (Rows.Contains(row))
            {
                row.SetChatsElsewhere(value.offer);
                row.SetSidebarStatus(value.status);
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
        string? problem;
        // Deleting touches a folder Claude may still be letting go of; whatever
        // goes wrong there is a warning to show, never a reason to crash.
        try { problem = App.Store.Delete(shortcut.Id, deletingProfile: deleteData.IsChecked == true); }
        catch (Exception e)
        {
            Diagnostics.Note("profile.removeFailed", new Dictionary<string, object?>
            {
                ["profile"] = shortcut.Folder, ["error"] = e.GetType().Name + ": " + e.Message,
            });
            problem = "Something went wrong removing it: " + e.Message;
        }
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
