using System.Runtime.InteropServices;
using Windows.Graphics;

namespace ClaudeGraft;

/// Where the flyout sits. A tray flyout belongs against the taskbar beside the
/// icon that opened it, so this reads the cursor — where the click just landed —
/// and the work area of the monitor under it, then tucks the window into the
/// corner the taskbar leaves, clamped so no edge runs off screen. The taskbar's
/// edge is read from how the work area is inset from the monitor, so a taskbar
/// on the top, left or right is followed as readily as the usual bottom one.
internal static class TrayAnchor
{
    private const int Margin = 8;

    /// Where the pointer is now, in physical pixels — read at the moment of the
    /// click, on the click's own thread, because by the time the window is built
    /// and measured the pointer has moved and the flyout would open under it
    /// rather than by the icon that was pressed.
    public static PointInt32 CursorNow()
    {
        GetCursorPos(out var p);
        return new PointInt32(p.X, p.Y);
    }

    /// The top-left corner, in physical pixels, for a window of <paramref name="size"/>
    /// opened from <paramref name="anchor"/> — the point the icon was clicked.
    public static PointInt32 Place(SizeInt32 size, PointInt32 anchor)
    {
        var point = new POINT { X = anchor.X, Y = anchor.Y };
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return new PointInt32(anchor.X - size.Width / 2, anchor.Y - size.Height - Margin);

        var work = info.rcWork;
        var full = info.rcMonitor;

        // Which edge the taskbar occupies shows in the side the work area is
        // pulled in from. Bottom is the common case and the fallback.
        int x, y;
        if (work.top > full.top)             // taskbar on top
        {
            y = work.top + Margin;
            x = anchor.X - size.Width / 2;
        }
        else if (work.left > full.left)      // taskbar on the left
        {
            x = work.left + Margin;
            y = anchor.Y - size.Height / 2;
        }
        else if (work.right < full.right)    // taskbar on the right
        {
            x = work.right - size.Width - Margin;
            y = anchor.Y - size.Height / 2;
        }
        else                                 // taskbar on the bottom
        {
            y = work.bottom - size.Height - Margin;
            x = anchor.X - size.Width / 2;
        }

        // Keep every edge inside the work area.
        x = Math.Clamp(x, work.left + Margin, work.right - size.Width - Margin);
        y = Math.Clamp(y, work.top + Margin, work.bottom - size.Height - Margin);
        return new PointInt32(x, y);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
}
