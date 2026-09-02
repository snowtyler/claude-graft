using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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

        // Mica: the theme-and-wallpaper backdrop of a modern Windows app. The
        // page background is transparent so this shows through behind the cards.
        SystemBackdrop = new MicaBackdrop();

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
        if (AppWindow is not null && AppWindowTitleBar.IsCustomizationSupported())
        {
            var isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
            AppWindow.TitleBar.PreferredTheme = isDark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        }

        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(900 * scale), (int)(620 * scale)));

        // Closing hides to the tray instead of quitting; Window.Closed is too
        // late to cancel, so this is on AppWindow.Closing.
        AppWindow.Closing += (sender, args) => { args.Cancel = true; sender.Hide(); };

        RootFrame.Navigate(typeof(MainPage));
    }

    public void Show()
    {
        AppWindow.Show();
        Activate();
    }
}
