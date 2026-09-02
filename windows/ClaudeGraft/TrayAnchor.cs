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

    /// The top-left corner, in physical pixels, for a window of <paramref name="size"/>.
    public static PointInt32 Place(SizeInt32 size)
    {
        GetCursorPos(out var cursor);

        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return new PointInt32(cursor.X - size.Width / 2, cursor.Y - size.Height - Margin);

        var work = info.rcWork;
        var full = info.rcMonitor;

        // Which edge the taskbar occupies shows in the side the work area is
        // pulled in from. Bottom is the common case and the fallback.
        int x, y;
        if (work.top > full.top)             // taskbar on top
        {
            y = work.top + Margin;
            x = cursor.X - size.Width / 2;
        }
        else if (work.left > full.left)      // taskbar on the left
        {
            x = work.left + Margin;
            y = cursor.Y - size.Height / 2;
        }
        else if (work.right < full.right)    // taskbar on the right
        {
            x = work.right - size.Width - Margin;
            y = cursor.Y - size.Height / 2;
        }
        else                                 // taskbar on the bottom
        {
            y = work.bottom - size.Height - Margin;
            x = cursor.X - size.Width / 2;
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
