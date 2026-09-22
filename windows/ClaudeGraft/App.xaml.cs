using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClaudeGraft.Core;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Toolkit.Uwp.Notifications;

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

    /// The app's preferences, applied by every window as it is built and again
    /// whenever they change. Held here, saved on change, and announced so the
    /// open windows re-dress themselves without being hunted down individually.
    public static GraftSettings Settings { get; private set; } = GraftSettings.Load();
    public static event Action? SettingsChanged;

    internal static App Instance => (App)Current;

    /// Held here so a background check and a manual one share one answer, and the
    /// flyout can show it the moment it opens rather than waiting on a fresh check.
    public static Updater.Release? AvailableUpdate { get; private set; }

    public static void ApplySettings(GraftSettings updated)
    {
        // Pressing Done with nothing touched should be as quiet as Cancel: no
        // save, and above all no re-apply, since reassigning a window's backdrop
        // flashes it even when the material is the same.
        if (updated.Theme == Settings.Theme
            && updated.Backdrop == Settings.Backdrop
            && updated.StartHidden == Settings.StartHidden) return;

        Settings = updated;
        updated.Save();
        SettingsChanged?.Invoke();
    }

    private TaskbarIcon? _tray;
    private MainWindow? _window;
    private FlyoutWindow? _flyout;
    private Timer? _warmTimer;
    private Timer? _updateTimer;
    private string? _notifiedVersion;

    // The tray's click callbacks arrive on H.NotifyIcon's message-window
    // thread, not this one; anything touching a WinUI window has to hop back to
    // the UI thread or it faults. Captured here, on the thread that owns the UI.
    private DispatcherQueue? _ui;

    public App()
    {
        // Wired before InitializeComponent and before OnLaunched build any window,
        // because the crash this catches is a managed throw on the XAML startup
        // path: it surfaces to the Windows event log as a stowed exception in
        // Microsoft.UI.Xaml with no type, message or stack, and left nothing at
        // all in this app's own diagnostics — a launch that freezes and dies with
        // no record of why. These three cover the three ways a throw goes
        // unobserved: the UI dispatcher, a background thread, and a dropped task.
        UnhandledException += (_, e) => Record("app.unhandled", e.Exception);
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => Record("domain.unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException +=
            (_, e) => Record("task.unobserved", e.Exception);

        InitializeComponent();
    }

    // Nothing is swallowed — the exception is written down and left to take the
    // process down as it would have, since a half-built window kept alive is a
    // worse state than the crash. The chain is walked so the real cause, wrapped
    // a few layers deep by the time WinUI marshals it, is not lost.
    private static void Record(string @event, Exception? error)
    {
        var fields = new Dictionary<string, object?>();
        var depth = 0;
        for (var e = error; e is not null && depth < 5; e = e.InnerException, depth++)
        {
            var suffix = depth == 0 ? "" : $".inner{depth}";
            fields["type" + suffix] = e.GetType().FullName;
            fields["message" + suffix] = e.Message;
            fields["stack" + suffix] = e.StackTrace;
        }
        if (error is null) fields["type"] = "(none)";
        Diagnostics.Note(@event, fields);
    }

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
            // Without this a left click is held back the length of the
            // double-click timeout, in case a second one follows — half a second
            // of nothing between pressing the icon and the flyout appearing.
            // Nothing here wants the double click, so the single one fires at once.
            NoLeftClickDelay = true,
        };
        // A left click opens the flyout — the account list with its usage, the
        // Mac menu bar item's whole face — while the right click keeps the plain
        // menu as a fallback that needs no window to draw.
        _tray.LeftClickCommand = new RelayCommand(ToggleFlyout);
        _tray.RightClickCommand = new RelayCommand(ShowMenu);
        _tray.ForceCreate();

        RegisterNotifications();

        // Built now, hidden, so the first left click shows it rather than paying
        // to construct a window and its backdrop before anything appears.
        _flyout = new FlyoutWindow(ShowManager, Quit);

        // A tray app comes up hidden by default — the notification-area icon is
        // the whole of it until asked for more. Turned off, it opens the manager
        // straight away, for someone who would rather see the window on launch.
        if (!Settings.StartHidden) ShowManager();

        // The one thing the app does off nobody's press: keep an opted-in
        // account's five-hour window open. It runs on its own timer, from launch,
        // window or no window — the app spends most of its life as a tray icon
        // with no manager window built at all, so the manager's usage timer, which
        // only ticks once that window exists, could never carry it. The threadpool
        // timer touches no UI, so it does not need one either. First pass after a
        // short beat, so a login-time launch is not asking the network before it
        // is up; every five minutes after, which the usage cache mostly answers
        // for free.
        _warmTimer = new Timer(
            _ => _ = SweepWarmProfiles(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5));

        // A minute in, so a login-time launch is not on the network before it is
        // up; every six hours after, well inside GitHub's unauthenticated rate
        // limit. A found update only lights the tray and the button — nothing here
        // installs on its own.
        _updateTimer = new Timer(
            _ => _ = CheckForUpdates(), null, TimeSpan.FromSeconds(60), TimeSpan.FromHours(6));
    }

    /// The classic toast path, not WinUI's AppNotificationManager, which needs a
    /// resource DLL the self-contained runtime does not ship and throws at
    /// registration. This one registers its own activator for an unpackaged app.
    private void RegisterNotifications()
    {
        try { ToastNotificationManagerCompat.OnActivated += OnToastActivated; }
        catch (Exception e) { Record("notify.register", e); }
    }

    private void ShowUpdateNotification(Updater.Release release)
    {
        try
        {
            new ToastContentBuilder()
                .AddText("Claude Graft update available")
                .AddText($"Version {release.Version} is ready to install.")
                .AddButton(new ToastButton().SetContent("Install").AddArgument("action", "install"))
                .AddButton(new ToastButton().SetContent("Dismiss").SetDismissActivation())
                .Show();
        }
        catch (Exception e) { Record("notify.show", e); }
    }

    // Only Install reaches here — Dismiss is handled by the toast itself — and the
    // callback is off a background thread, so it hops to the UI thread to act.
    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        if (!args.TryGetValue("action", out var action) || action != "install") return;
        OnUi(() => { if (AvailableUpdate is { } release) _ = InstallUpdateAsync(release); });
    }

    private async Task CheckForUpdates()
    {
        try
        {
            var found = await Updater.CheckAsync();
            OnUi(() => SetAvailableUpdate(found));
        }
        catch (Exception e) { Record("update.check", e); }
    }

    /// Throws through to the caller so a failed check can be told from one that
    /// found nothing — the button says something different for each.
    internal async Task<Updater.Release?> RunUpdateCheckAsync()
    {
        try
        {
            var found = await Updater.CheckAsync();
            SetAvailableUpdate(found);
            return found;
        }
        catch (Exception e)
        {
            Record("update.check", e);
            throw;
        }
    }

    private void SetAvailableUpdate(Updater.Release? release)
    {
        AvailableUpdate = release;

        // The notify half of the feature: the icon a person is not looking at
        // still says an update is waiting. Once per version, so a check every six
        // hours does not keep rewriting the same tooltip.
        var version = release?.Version.ToString();
        if (version is not null && version != _notifiedVersion)
        {
            _notifiedVersion = version;
            if (_tray is not null) _tray.ToolTipText = $"Claude Graft — update {version} ready";
            ShowUpdateNotification(release!);
        }
        else if (release is null && _tray is not null)
        {
            _tray.ToolTipText = "Claude Graft";
        }
    }

    /// Fetches the installer and hands off to it, then quits — the installer
    /// cannot replace a running exe, so the process that holds it open has to go.
    /// The Inno installer brings the app back once it finishes.
    internal async Task InstallUpdateAsync(Updater.Release release)
    {
        var path = await Updater.DownloadAsync(release);
        Diagnostics.Note("update.install",
            new Dictionary<string, object?> { ["version"] = release.Version.ToString(), ["path"] = path });
        Updater.LaunchInstaller(path);
        OnUi(() =>
        {
            _tray?.Dispose();
            Exit();
        });
    }

    private async Task SweepWarmProfiles()
    {
        try { await AutoStarter.SweepAsync(WarmProfiles()); }
        catch (Exception e) { Record("autostart.sweep", e); }
    }

    /// The profiles whose owners asked to keep their window open — the main Claude
    /// when its box is ticked, and each grafted shortcut carrying the flag. Read
    /// off the list already in memory rather than reloaded, so a sweep on the
    /// threadpool thread does not reassign the collection the windows are drawing.
    private static IEnumerable<string> WarmProfiles()
    {
        var profiles = new List<string>();
        if (Settings.KeepMainWarm) profiles.Add(GraftPaths.DefaultProfile);
        // Snapshot first: this runs on the threadpool while the windows may be
        // editing the list, and enumerating one mid-change throws.
        profiles.AddRange(Store.Shortcuts.ToList().Where(s => s.KeepWarm).Select(s => s.ProfileDir));
        return profiles;
    }

    private void ToggleFlyout()
    {
        // Read on this thread, the click's own, before hopping to the UI thread:
        // the pointer is on the icon now and will have moved by the time the
        // window is measured and placed. The foreground window is read here too,
        // as early as the click allows, so the flyout can hand focus back to the
        // app the person was in when it dismisses — before the click has had a
        // chance to move it to the shell.
        var anchor = TrayAnchor.CursorNow();
        var priorForeground = GetForegroundWindow();
        OnUi(() =>
        {
            _flyout ??= new FlyoutWindow(ShowManager, Quit);
            _flyout.Toggle(anchor, priorForeground);
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

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
        // The Mac's wording: "Version X is available" installs it, otherwise the
        // plain "Check for Updates".
        items.Add(AvailableUpdate is { } ready
            ? ($"Version {ready.Version} is available", true, () => _ = InstallUpdateAsync(ready))
            : ("Check for Updates", true, () => _ = CheckForUpdates()));
        items.Add(("Manage Profiles…", true, ShowManager));
        items.Add(("Settings…", true, ShowSettings));
        items.Add(("Quit", true, Quit));

        TrayMenu.Show(items);
    }

    private void ShowManager() => OnUi(() =>
    {
        _window ??= new MainWindow();
        _window.Show();
    });

    /// Settings live in a dialog on the manager window, so opening them from the
    /// tray brings the window up first — a dialog needs a window to sit in.
    private void ShowSettings() => OnUi(() =>
    {
        _window ??= new MainWindow();
        _window.ShowSettings();
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
