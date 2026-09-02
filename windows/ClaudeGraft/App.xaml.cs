using System.Threading.Tasks;
using ClaudeGraft.Core;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClaudeGraft;

/// <summary>
/// Claude Graft lives in the notification area, not in a window — the same
/// tray-first shape the Mac build has in the menu bar. It comes up with no
/// window shown; the tray icon's menu opens a profile or the manager, and the
/// manager window hides back to the tray rather than quitting.
/// </summary>
public partial class App : Application
{
    public static ShortcutStore Store { get; } = new();

    private TaskbarIcon? _tray;
    private MainWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {

        _tray = new TaskbarIcon
        {
            ToolTipText = "Claude Graft",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
            ContextFlyout = BuildMenu(),
            NoLeftClickDelay = true,
        };
        // Left-click opens the manager; right-click shows the quick menu.
        _tray.LeftClickCommand = new RelayCommand(ShowManager);
        _tray.ForceCreate();
    }

    /// Rebuilt each time it opens, since the profile list changes underneath it.
    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => Populate(menu);
        Populate(menu);
        return menu;
    }

    private void Populate(MenuFlyout menu)
    {
        menu.Items.Clear();
        Store.Load();

        if (Store.Shortcuts.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "No profiles yet", IsEnabled = false });
        }
        else
        {
            foreach (var shortcut in Store.Shortcuts)
            {
                var item = new MenuFlyoutItem { Text = "Open " + shortcut.Name };
                var config = Store.ConfigFor(shortcut);
                item.Click += (_, _) => Task.Run(() => Launcher.Open(config));
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = "Manage Profiles…" };
        manage.Click += (_, _) => ShowManager();
        menu.Items.Add(manage);

        var quit = new MenuFlyoutItem { Text = "Quit" };
        quit.Click += (_, _) => Quit();
        menu.Items.Add(quit);
    }

    private void ShowManager()
    {
        _window ??= new MainWindow();
        _window.Show();
    }

    private void Quit()
    {
        _tray?.Dispose();
        Exit();
    }
}

/// A minimal ICommand for the tray's left-click, which takes no parameter.
public sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
