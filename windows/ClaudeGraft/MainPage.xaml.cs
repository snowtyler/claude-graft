using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

/// One row in the manager: a profile, where it reads its chats from, and the
/// folder its data lives in.
public sealed class ShortcutRow
{
    public Shortcut Shortcut { get; set; } = null!;
    public string Name => Shortcut.Name;
    public string SourceLabel => App.Store.Label(Shortcut.Source);
    public string Folder => Shortcut.Folder;
}

public sealed partial class MainPage : Page
{
    public ObservableCollection<ShortcutRow> Rows { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        App.Store.Load();
        Rows.Clear();
        foreach (var shortcut in App.Store.Shortcuts)
            Rows.Add(new ShortcutRow { Shortcut = shortcut });   // set via initializer
        EmptyState.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutRow row })
        {
            var config = App.Store.ConfigFor(row.Shortcut);
            Task.Run(() => Launcher.Open(config));
        }
    }
}
