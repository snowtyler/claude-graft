using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

/// The tray flyout's content: each profile as a compact card with its usage,
/// and the actions that used to live in the right-click menu. It raises the
/// window-level ones — showing the manager, quitting — rather than reaching for
/// the app itself, and dismisses the flyout once a card's Open is pressed.
public sealed partial class FlyoutView : UserControl
{
    public ObservableCollection<ShortcutRow> Rows { get; } = new();

    /// Raised for the actions the host window owns; the flyout only knows it
    /// wants them, not how the app carries them out.
    public event Action? OpenManagerRequested;
    public event Action? QuitRequested;
    public event Action? DismissRequested;

    /// The content changed size — a row added, an account's bars arriving — so
    /// the host window should measure again and refit. The view is stretched to
    /// the window, so its own SizeChanged cannot see the overflow; this says so.
    public event Action? LayoutChanged;

    private Updater.Release? _update;

    public FlyoutView()
    {
        InitializeComponent();
        App.UpdateProgressChanged += ReflectUpdate;
        Setup.RecheckRequested += Reload;
        Setup.Acted += () => DismissRequested?.Invoke();
        UsageMonitor.FetchingChanged += (profile, fetching) => DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var row in Rows.Where(r => Fs.SamePath(r.ProfileDir, profile)))
                row.SetFetching(fetching);
            LayoutChanged?.Invoke();
        });
    }

    /// Paints the flyout's own surface opaque, for the Solid backdrop where there
    /// is no material behind it, or leaves it transparent so a material shows
    /// through. The window's mica is what the transparent case reveals.
    public void SetOpaqueSurface(bool opaque) =>
        OpaqueSurface.Visibility = opaque ? Visibility.Visible : Visibility.Collapsed;

    /// Rebuilds the list and refreshes usage. Called each time the flyout opens,
    /// so the figures are current the way pressing the Mac menu bar item makes
    /// them — interactive, since a person is looking.
    public void Reload()
    {
        Rows.Clear();
        App.Store.Load();
        var state = Onboarding.Check(App.Store);
        var ready = state == SetupState.Ready;
        Setup.Show(state);
        ProfileScroller.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        var rows = ready ? ProfileRows.Build() : new List<ShortcutRow>();
        foreach (var row in rows)
        {
            Rows.Add(row);
            _ = LoadUsage(row);
        }
        ReflectUpdate();
        LayoutChanged?.Invoke();
        _ = MarkRunning(rows);
    }

    private void ReflectUpdate()
    {
        _update = App.AvailableUpdate;
        CheckButton.IsEnabled = true;
        CheckButton.Content = "Check for Updates…";
        var downloading = App.UpdateDownload;
        AvailableButton.IsEnabled = downloading is null;
        AvailableButton.Content = _update is null
            ? null
            : downloading is { } f
                ? $"Downloading version {_update.Version}… {(int)Math.Round(f * 100)}%"
                : $"Version {_update.Version} is available";
        AvailableButton.Visibility = _update is null ? Visibility.Collapsed : Visibility.Visible;
        var showBar = downloading is not null && _update is not null;
        if (showBar) UpdateProgress.Value = downloading!.Value;
        if ((UpdateProgress.Visibility == Visibility.Visible) != showBar)
        {
            UpdateProgress.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
            LayoutChanged?.Invoke();
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        CheckButton.Content = "Checking for Updates…";
        // A failed check is left silent, the way the Mac's is; the reason is in diagnostics.
        try { await App.Instance.RunUpdateCheckAsync(); }
        catch { }
        ReflectUpdate();
        LayoutChanged?.Invoke();
    }

    /// The flyout stays up so the download can be watched; the progress itself
    /// arrives through ReflectUpdate, and a failure puts the offer back.
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        await App.Instance.InstallUpdateAsync(_update);
    }

    /// The dots come in after the window is up, not before it: reading which
    /// Claude is running is a WMI query, slow enough that doing it inline made
    /// the flyout crawl open. It runs off the UI thread and lights the dots when
    /// it lands — a beat late is not something the eye catches on a status dot.
    private async Task MarkRunning(IReadOnlyList<ShortcutRow> rows)
    {
        var processes = await Task.Run(ClaudeProcesses.Enumerate);
        foreach (var row in rows)
            if (Rows.Contains(row)) row.SetRunning(ClaudeProcesses.IsRunning(row.ProfileDir, processes));
    }

    private async Task LoadUsage(ShortcutRow row)
    {
        var entry = await ProfileRows.ReadUsageSafe(row.ProfileDir, interactive: true);
        // Back on the UI thread after the await; the row may have been cleared
        // by a reload since.
        if (!Rows.Contains(row)) return;
        if (entry is not null)
        {
            row.SetUsage(entry);
            LayoutChanged?.Invoke();   // the bars just appeared; the flyout is taller now
        }
        else
        {
            // No reading at all: un-gate Start Session rather than leaving it
            // held on a usage figure that never comes.
            row.MarkUsageUnavailable();
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutRow row })
        {
            ProfileRows.Open(row);
            DismissRequested?.Invoke();
        }
    }


    /// Opens a five-hour window on one account by sending it a single short
    /// message, the way the Mac dropdown's per-row Start Session does. Only a
    /// press reaches here; the button gives way to a spinner while it runs and a
    /// second press is refused, so a window that is already opening is not asked
    /// for twice.
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShortcutRow row } || row.Starting) return;
        row.Problem = null;
        row.Starting = true;
        var profile = row.ProfileDir;

        var problem = await Task.Run(() => SessionStarter.StartAsync(profile));

        // The window this just opened is exactly what the stored reading predates,
        // so it is dropped rather than waited out, then the row's usage re-read.
        UsageMonitor.Invalidate(profile);
        row.Starting = false;
        row.Problem = problem;
        if (Rows.Contains(row)) await LoadUsage(row);
        LayoutChanged?.Invoke();
    }

    /// Re-reads every account's usage from the endpoint, skipping the cache and
    /// the backoff the way the Mac's Refresh Usage does — a figure someone is
    /// looking at and pressed for.
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "Refreshing…";
        try
        {
            var rows = Rows.ToList();
            await Task.WhenAll(rows.Select(LoadUsage));
            await MarkRunning(rows);
        }
        finally
        {
            RefreshButton.Content = "Refresh Usage";
            RefreshButton.IsEnabled = true;
        }
    }

    private void Manager_Click(object sender, RoutedEventArgs e)
    {
        OpenManagerRequested?.Invoke();
        DismissRequested?.Invoke();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();
}
