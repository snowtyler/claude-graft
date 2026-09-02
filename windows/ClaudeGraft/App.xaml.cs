using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClaudeGraft.Core;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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
    private FlyoutWindow? _flyout;

    // The tray's click callbacks arrive on H.NotifyIcon's message-window
    // thread, not this one; anything touching a WinUI window has to hop back to
    // the UI thread or it faults. Captured here, on the thread that owns the UI.
    private DispatcherQueue? _ui;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();

        // No ContextFlyout: the library renders that as a native popup owned by
        // its own message-only window, which can never take the foreground a
        // popup menu needs to register clicks — the menu draws but every
        // selection is dropped. The right-click menu is built and shown by hand
        // instead, from TrayMenu, with an owner window that can go foreground.
        _tray = new TaskbarIcon
        {
            ToolTipText = "Claude Graft",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
        };
        // A left click opens the flyout — the account list with its usage, the
        // Mac menu bar item's whole face — while the right click keeps the plain
        // menu as a fallback that needs no window to draw.
        _tray.LeftClickCommand = new RelayCommand(ToggleFlyout);
        _tray.DoubleClickCommand = new RelayCommand(ShowManager);
        _tray.RightClickCommand = new RelayCommand(ShowMenu);
        _tray.ForceCreate();
    }

    private void ToggleFlyout() => OnUi(() =>
    {
        _flyout ??= new FlyoutWindow(ShowManager, Quit);
        _flyout.Toggle();
    });

    private void ShowMenu()
    {
        Store.Load();

        var items = new List<(string Text, bool Enabled, Action? Invoke)>();
        if (Store.Shortcuts.Count == 0)
        {
            items.Add(("No profiles yet", false, null));
        }
        else
        {
            foreach (var shortcut in Store.Shortcuts)
            {
                var config = Store.ConfigFor(shortcut);
                items.Add(("Open " + shortcut.Name, true, () => Task.Run(() => Launcher.Open(config))));
            }
        }
        items.Add((TrayMenu.Separator, false, null));
        items.Add(("Manage Profiles…", true, ShowManager));
        items.Add(("Quit", true, Quit));

        TrayMenu.Show(items);
    }

    private void ShowManager() => OnUi(() =>
    {
        _window ??= new MainWindow();
        _window.Show();
    });

    private void Quit() => OnUi(() =>
    {
        _tray?.Dispose();
        Exit();
    });

    /// Runs on the UI thread whether the caller is already there or on the
    /// tray's message-window thread.
    private void OnUi(Action action)
    {
        if (_ui is null || _ui.HasThreadAccess) action();
        else _ui.TryEnqueue(() => action());
    }
}

/// A minimal ICommand for the tray's double- and right-click, which take no parameter.
public sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}

/// <summary>
/// A native Win32 popup menu at the cursor. A popup menu only registers clicks
/// while its owner window holds the foreground, and the tray's own window is
/// message-only and cannot — so this shows a throwaway, fully transparent
/// top-level window at the cursor, makes it foreground, and owns the menu with
/// it. TrackPopupMenuEx returns the chosen id, so no WndProc is needed.
/// </summary>
internal static class TrayMenu
{
    internal const string Separator = "__graft-menu-separator__";

    public static void Show(List<(string Text, bool Enabled, Action? Invoke)> items)
    {
        nint owner = CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TOPMOST, "STATIC", string.Empty,
            WS_POPUP, 0, 0, 1, 1, 0, 0, 0, 0);
        if (owner == 0) return;

        nint menu = CreatePopupMenu();
        if (menu == 0) { DestroyWindow(owner); return; }

        try
        {
            var actions = new List<Action?>();
            uint id = 1;
            foreach (var (text, enabled, invoke) in items)
            {
                if (text == Separator)
                {
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                    continue;
                }
                uint flags = MF_STRING | (enabled ? 0u : MF_GRAYED);
                AppendMenu(menu, flags, id, text);
                actions.Add(invoke);
                id++;
            }

            GetCursorPos(out var pt);

            // The window has to be visible for SetForegroundWindow to take, so
            // it is shown — but at one transparent pixel it is shown to nobody.
            SetLayeredWindowAttributes(owner, 0, 0, LWA_ALPHA);
            ShowWindow(owner, SW_SHOW);
            SetForegroundWindow(owner);

            uint chosen = TrackPopupMenuEx(menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, owner, 0);

            // The documented trailer: without a message posted to the owner, the
            // menu can leave a stale mouse-capture that eats the next click.
            PostMessage(owner, WM_NULL, 0, 0);

            if (chosen >= 1 && chosen <= actions.Count)
                actions[(int)chosen - 1]?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
            DestroyWindow(owner);
        }
    }

    private const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_SEPARATOR = 0x0800;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x0080, WS_EX_LAYERED = 0x00080000, WS_EX_TOPMOST = 0x0008;
    private const uint LWA_ALPHA = 0x0002;
    private const int SW_SHOW = 5;
    private const uint WM_NULL = 0x0000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int cmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint hMenu, uint flags, nuint idNewItem, string? newItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(nint hMenu, uint flags, int x, int y, nint hWnd, nint tpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
}
