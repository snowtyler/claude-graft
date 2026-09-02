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

    public FlyoutWindow(Action openManager, Action quit)
    {
        Content = _view;
        _view.OpenManagerRequested += openManager;
        _view.QuitRequested += quit;
        _view.DismissRequested += Hide;

        // Borderless, always-on-top, out of the taskbar and the switcher — the
        // shape a context menu gets, which is exactly a flyout's.
        AppWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());
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

    public void Toggle()
    {
        if (_shown) Hide();
        else Show();
    }

    private void Show()
    {
        _shown = true;
        _view.Reload();
        Refit();
        AppWindow.Show(activateWindow: true);
        Activate();
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

        var at = TrayAnchor.Place(size);
        AppWindow.MoveAndResize(new RectInt32(at.X, at.Y, size.Width, size.Height));
    }

    private void RoundCorners()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
