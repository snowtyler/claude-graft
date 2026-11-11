using System.Runtime.InteropServices;
using ClaudeGraft.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace ClaudeGraft;

/// <summary>
/// The profile manager. Closing it hides it back to the tray rather than
/// quitting — the app keeps running in the notification area, as the Mac build
/// keeps running in the menu bar. Quit is a deliberate act from the tray menu.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // The icon ships packed as an app resource, not a loose file, so this
        // path is absent in an unpackaged install — and SetIcon throwing on it
        // used to abort the rest of the constructor, navigation included, which
        // is a window that opens with no content behind the title bar. A window
        // titled by its resource icon is fine without this; a missing decoration
        // must not cost the profile list.
        var iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets/AppIcon.ico");
        if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);

        ApplyAppearance();
        App.SettingsChanged += ApplyAppearance;
        // The window only hides, so this outlives every close, but drop the
        // handler if it is ever truly closed rather than leak onto a dead window.
        Closed += (_, _) => App.SettingsChanged -= ApplyAppearance;

        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        // Tall enough for two cards without scrolling, but never past a small or
        // heavily scaled screen, and centred so the extra height is not pushed off it.
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1100 * scale), work.Width);
        var height = Math.Min((int)(670 * scale), (int)(work.Height * 0.9));
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));

        // Closing hides to the tray instead of quitting; Window.Closed is too
        // late to cancel, so this is on AppWindow.Closing.
        AppWindow.Closing += (sender, args) => { args.Cancel = true; sender.Hide(); };

        RootFrame.Navigate(typeof(MainPage));
        if (RootFrame.Content is MainPage page) page.ClaudesChanged += RebuildClaudes;
        RebuildClaudes();
    }

    /// The chosen Claude's name and state, or the section's name, as the Mac's
    /// window title and subtitle.
    private void ShowHeading()
    {
        var tag = (Sections.SelectedItem as NavigationViewItem)?.Tag as string;
        (string Title, string Subtitle) heading = tag switch
        {
            "artifacts" => ("Artifacts", "Every account"),
            "projects" => ("Projects", "Every account"),
            "search" => ("Search Chats", "Every account"),
            _ => RootFrame.Content is MainPage page ? page.Heading : ("Claude", ""),
        };
        HeadingTitle.Text = heading.Title;
        HeadingSubtitle.Text = heading.Subtitle;
        Title = heading.Title == "Claude Graft" ? heading.Title : $"{heading.Title} · Claude Graft";
    }

    // Hidden outright, as the Mac's is: folded to WinUI's icon rail, the two-line items stretched it into a scroll.
    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        Sections.IsPaneVisible = !Sections.IsPaneVisible;
        var open = Sections.IsPaneVisible;
        SidebarColumn.Width = new GridLength(open ? Sections.OpenPaneLength : 0);
        SidebarSpacer.Width = open ? Sections.OpenPaneLength - 48 : 0;
        NewShortcutButton.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void NewShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is not MainPage page) return;
        ShowClaudes();
        await page.AddAsync();
    }

    private bool _rebuilding;
    private string _builtSignature = "";

    /// One entry per Claude, from the page's own rows, so the sidebar and the page
    /// can never disagree about which Claudes there are.
    private void RebuildClaudes()
    {
        if (RootFrame.Content is not MainPage page || _rebuilding) return;
        NewClaudeItem.Visibility = page.SetupReady ? Visibility.Visible : Visibility.Collapsed;
        // A draft is no Claude in the list yet, so nothing there is chosen.
        if (page.Drafting && Sections.SelectedItem is not null)
        {
            _rebuilding = true;
            Sections.SelectedItem = null;
            _rebuilding = false;
        }
        else if (!page.Drafting && Sections.SelectedItem is null) SelectFocused(page);
        // Entries made afresh have no indicator to slide from, so a choice that
        // changed no Claude leaves them where they are.
        var signature = string.Join("\n", page.Rows.Select(r => $"{r.Key}|{r.Name}|{r.SourceLabel}|{r.Shortcut?.Source.Kind}|{r.Shortcut?.InstalledName}"));
        if (signature == _builtSignature) return;
        _builtSignature = signature;
        _rebuilding = true;
        try
        {
            var items = Sections.MenuItems;
            foreach (var old in items.OfType<NavigationViewItem>().Where(i => ((string?)i.Tag)?.StartsWith("claude:") == true).ToList())
                items.Remove(old);
            var showingClaude = Sections.SelectedItem is not NavigationViewItem { Tag: string t } || t.StartsWith("claude:");
            // Claude heads the first section, as on the Mac; the shortcuts have their own.
            var mainAt = items.IndexOf(ClaudesHeader) + 1;
            var shortcutAt = items.IndexOf(ShortcutsHeader) + 1;
            foreach (var row in page.Rows.ToList())
            {
                var line = row.Shortcut is null ? "Installed normally"
                    : row.Shortcut.InstalledName is null ? "Not created yet" : row.SourceLabel;
                var words = new StackPanel { Spacing = 1 };
                words.Children.Add(new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                words.Children.Add(new TextBlock
                {
                    Text = line, TextTrimming = TextTrimming.CharacterEllipsis,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
                var glyph = row.Shortcut is null ? "\uE80F" : row.Shortcut.Source.Kind == SourceKind.Own ? "\uEA3A" : "\uE71B";
                var item = new NavigationViewItem { Content = words, Tag = "claude:" + row.Key, Icon = new FontIcon { Glyph = glyph } };
                AutomationProperties.SetName(item, $"{row.Name}, {line}");
                AutomationProperties.SetAutomationId(item, "sidebar." + row.Key);
                if (row.Shortcut is null)
                {
                    items.Insert(mainAt, item);
                    shortcutAt++;
                }
                else items.Insert(shortcutAt++, item);
                if (showingClaude && row.Key == page.Focused) Sections.SelectedItem = item;
            }
        }        finally { _rebuilding = false; }
    }

    private void SelectFocused(MainPage page)
    {
        var tag = "claude:" + page.Focused;
        if (Sections.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag) is { } item)
        {
            _rebuilding = true;
            Sections.SelectedItem = item;
            _rebuilding = false;
        }
    }

    private async void Sections_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var tag = (args.InvokedItemContainer as NavigationViewItem)?.Tag as string;
        if (RootFrame.Content is not MainPage page) return;
        if (tag == "new")
        {
            ShowClaudes();
            await page.AddAsync();
        }
        else if (tag == "settings") await page.OpenSettingsAsync();
    }

    private async void ClaudeRestart_Requested()
    {
        if (RootFrame.Content is MainPage page) await page.ConfirmRestartClaudesAsync();
    }

    private void Sections_SelectionChanged(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.Tag as string;
        var claudes = tag is null || tag.StartsWith("claude:");
        DispatcherQueue.TryEnqueue(ShowHeading);
        if (claudes && !_rebuilding && tag is not null && RootFrame.Content is MainPage page) page.Focus(tag["claude:".Length..]);
        RootFrame.Visibility = claudes ? Visibility.Visible : Visibility.Collapsed;
        SectionFrame.Visibility = claudes ? Visibility.Collapsed : Visibility.Visible;
        if (tag == "artifacts")
        {
            if (SectionFrame.Content is ArtifactsPage shown) shown.OnShown();
            else SectionFrame.Navigate(typeof(ArtifactsPage));
        }
        else if (tag == "projects" && SectionFrame.Content is not ProjectsPage)
        {
            SectionFrame.Navigate(typeof(ProjectsPage));
        }
        else if (tag == "search" && SectionFrame.Content is not SearchPage)
        {
            SectionFrame.Navigate(typeof(SearchPage));
        }
    }

    /// A Claude's page again, for the tray's entries that act on that page; the
    /// one already chosen stays chosen.
    private void ShowClaudes()
    {
        if (Sections.SelectedItem is NavigationViewItem { Tag: string tag } && tag.StartsWith("claude:")) return;
        var focused = RootFrame.Content is MainPage page ? "claude:" + page.Focused : "claude:main";
        Sections.SelectedItem = Sections.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == focused)
            ?? Sections.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => ((string?)i.Tag)?.StartsWith("claude:") == true);
    }

    public void Show()
    {
        AppWindow.Show();
        Activate();
        // The page loads its usage once, when it is first built; every reopen
        // after that has to ask for a fresh read itself, or a first read that
        // came up empty would never be retried. Harmless on the first show, when
        // the list is still filling and there is nothing yet to refresh.
        if (RootFrame.Content is MainPage page) page.OnShown();
    }

    /// Brings the window up and opens the settings dialog on it — the tray's
    /// Settings entry has no window of its own to show one in.
    public void ConfirmRestartClaudes()
    {
        Show();
        ShowClaudes();
        if (RootFrame.Content is MainPage page) _ = page.ConfirmRestartClaudesAsync();
    }

    public void ShowSettings()
    {
        Show();
        ShowClaudes();
        if (RootFrame.Content is MainPage page) _ = page.OpenSettingsAsync();
    }

    /// Dresses the window from the current settings: theme on the content root,
    /// the chosen backdrop, and the title bar to match. Backdrop None leaves the
    /// window with no material, so the root paints an opaque themed surface —
    /// otherwise the window would be see-through onto the desktop.
    private BackdropMaterial? _appliedBackdrop;

    private void ApplyAppearance()
    {
        var settings = App.Settings;
        RootGrid.RequestedTheme = Appearance.ToElementTheme(settings.Theme);

        // Only when the material actually changes: assigning SystemBackdrop
        // re-composites the window, which reads as a flash if done for a Done that
        // left the backdrop where it was.
        if (_appliedBackdrop != settings.Backdrop)
        {
            var backdrop = Appearance.ToBackdrop(settings.Backdrop);
            SystemBackdrop = backdrop;
            _appliedBackdrop = settings.Backdrop;
        }

        if (AppWindow is not null && AppWindowTitleBar.IsCustomizationSupported())
            AppWindow.TitleBar.PreferredTheme = settings.Theme switch
            {
                AppTheme.Dark => TitleBarTheme.Dark,
                AppTheme.Light => TitleBarTheme.Light,
                // Not Application.RequestedTheme, which is fixed at startup and
                // left the bar on the old theme after Windows switched.
                _ => TitleBarTheme.UseDefaultAppMode,
            };
    }
}
