using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace ClaudeGraft;

/// The tray flyout, as a window rather than a control. WinUI 3 gives a tray icon
/// no popup of its own, so this is a borderless, always-on-top window shaped and
/// coloured to read as a flyout — acrylic behind, rounded, hairline-bordered by
/// the view inside it — that shows itself against the taskbar beside the icon and
/// hides the moment it loses focus, the way a menu does. It is built once and
/// reused: showing it again refreshes the list rather than making a new window.
public sealed class FlyoutWindow : Window
{
    private readonly FlyoutView _view = new();
    private bool _shown;
    private PointInt32 _anchor;

    public FlyoutWindow(Action openManager, Action quit)
    {
        Content = _view;
        _view.OpenManagerRequested += openManager;
        _view.QuitRequested += quit;
        _view.DismissRequested += Hide;

        // Borderless, always-on-top, out of the taskbar and the switcher. Not
        // the context-menu presenter it looks like it wants: that one shows
        // without ever activating the window, so it never hears the click that
        // lands elsewhere and never dismisses. A plain overlapped presenter
        // activates when shown, which is what makes losing focus mean something.
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        RoundCorners();

        // The list grows after it is shown, as each account's usage arrives and
        // its bars appear. The view is stretched to the window and cannot see its
        // own overflow, so it says when it has changed size and the window refits.
        _view.LayoutChanged += () => { if (_shown) Refit(); };

        // Esc dismisses wherever focus sits, so it rides an accelerator rather
        // than a key handler on one focused element.
        var esc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = VirtualKey.Escape };
        esc.Invoked += (_, e) => { e.Handled = true; Hide(); };
        _view.KeyboardAccelerators.Add(esc);

        // Losing focus is the light-dismiss: a click anywhere else takes it away.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) Hide();
        };
    }

    public void Toggle(PointInt32 anchor)
    {
        if (_shown) Hide();
        else Show(anchor);
    }

    private void Show(PointInt32 anchor)
    {
        _anchor = anchor;
        _shown = true;
        _view.Reload();
        Refit();
        AppWindow.Show(activateWindow: true);
        Activate();
        // A tray click leaves the shell in the foreground, not this process, so
        // the window comes up without focus and would never hear it being lost.
        // Pulling it to the foreground is what arms the click-away dismiss.
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private void Hide()
    {
        if (!_shown) return;
        _shown = false;
        AppWindow.Hide();
    }

    /// Sizes the window to the content and tucks it against the taskbar.
    private void Refit()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;

        _view.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = new SizeInt32(
            (int)Math.Ceiling(_view.DesiredSize.Width * scale),
            (int)Math.Ceiling(_view.DesiredSize.Height * scale));
        if (size.Width <= 0 || size.Height <= 0) return;

        var at = TrayAnchor.Place(size, _anchor);
        AppWindow.MoveAndResize(new RectInt32(at.X, at.Y, size.Width, size.Height));
    }

    private void RoundCorners()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        // Windows draws its own light border around a top-level window; on a
        // flyout it reads as a stray white outline around the panel, so it is
        // turned off and the view's own hairline is the only edge.
        int none = unchecked((int)DWMWA_COLOR_NONE);
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}
