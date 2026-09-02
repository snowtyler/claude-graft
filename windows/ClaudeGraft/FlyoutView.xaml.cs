using System.Collections.ObjectModel;
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

    public FlyoutView() => InitializeComponent();

    /// Rebuilds the list and refreshes usage. Called each time the flyout opens,
    /// so the figures are current the way pressing the Mac menu bar item makes
    /// them — interactive, since a person is looking.
    public void Reload()
    {
        Rows.Clear();
        var rows = ProfileRows.Build();
        foreach (var row in rows)
        {
            Rows.Add(row);
            _ = LoadUsage(row);
        }
        LayoutChanged?.Invoke();
        _ = MarkRunning(rows);
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
        if (entry is not null && Rows.Contains(row))
        {
            row.SetUsage(entry);
            LayoutChanged?.Invoke();   // the bars just appeared; the flyout is taller now
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
            DismissRequested?.Invoke();
        }
    }

    private void Manager_Click(object sender, RoutedEventArgs e)
    {
        OpenManagerRequested?.Invoke();
        DismissRequested?.Invoke();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();
}
