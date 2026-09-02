using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ClaudeGraft.Core;
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
            Rows.Add(new ShortcutRow { Shortcut = shortcut });
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

    private async void Add_Click(object sender, RoutedEventArgs e) => await EditProfile(null);

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutRow row }) await EditProfile(row.Shortcut);
    }

    private async Task EditProfile(Shortcut? existing)
    {
        var dialog = new ProfileDialog(App.Store, existing) { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            if (dialog.IsNew) App.Store.Add(dialog.Result);
            else App.Store.Update(dialog.Result);

            // Bring the new profile's storage in line — grafts if it has a
            // source, ungrafts if it keeps its own chats — off the UI thread,
            // then refresh the list.
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
