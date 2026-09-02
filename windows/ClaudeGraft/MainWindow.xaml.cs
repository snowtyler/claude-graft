using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
        AppWindow.SetIcon(System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets/AppIcon.ico"));
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
