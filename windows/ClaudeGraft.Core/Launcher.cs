using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeGraft.Core;

/// <summary>
/// Opens a profile's Claude Desktop. A minimal stand-in for the Mac build's full
/// launch layer: bring the profile's storage in line with its configuration,
/// then either show the Claude already on it or start a new one. Records are not
/// yet swept here — that wiring comes with the launcher proper.
/// </summary>
public static class Launcher
{
    /// The Claude Desktop binary, newest installed version. Squirrel keeps each
    /// version in its own app-<version> folder under LocalAppData.
    public static string? ClaudeExe()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root, "app-*")
            .Select(d => Path.Combine(d, "claude.exe"))
            .Where(File.Exists)
            .OrderByDescending(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// Open the profile a shortcut folder names, resolving its source from the
    /// shared list — which is what a desktop shortcut hands the launcher stub.
    /// An unknown folder opens on its own chats rather than refusing, so a
    /// shortcut whose list entry has gone still opens its profile.
    public static void OpenByFolder(string folder)
    {
        var store = new ShortcutStore();
        var shortcut = store.Shortcuts.FirstOrDefault(s => s.Folder == folder);
        var config = shortcut is not null
            ? store.ConfigFor(shortcut)
            : new GraftConfig { ProfileDir = GraftPaths.Profile(folder), SourceDir = null };
        Open(config);
    }

    /// Bring the storage in line and open Claude. If one is already on the
    /// profile, show it rather than starting a second — two Claudes on one chat
    /// store both write it, which is the one loss this app exists to avoid.
    public static void Open(GraftConfig config)
    {
        var filing = FilingProfiles(config);
        if (ClaudeProcesses.IsRunning(config.ProfileDir))
        {
            if (ClaudeProcesses.ProcessIdentifier(config.ProfileDir) is int pid) Reveal(pid);
            // A profile already open is the one case nothing else covers: no
            // launch to file records on the way to, and no timer behind it. So
            // the sweep runs on the way past instead, after the window is up.
            SquareUp(filing);
            return;
        }
        // Apply first, so a record filed through a graft lands where the link now
        // points; sweep before launch, so the records are on disk before Claude
        // reads its sidebar as it comes up.
        Graft.Apply(config);
        SquareUp(filing);
        Launch(config.ProfileDir);
    }

    /// Mirror every known pair back into line, then file the records for any
    /// session whose transcript survived without one. The state report the Mac
    /// writes here is not ported yet; the mirror and the record sweep are what a
    /// launch has to do before a sidebar is built.
    private static void SquareUp(IReadOnlyList<string> filingInto)
    {
        Graft.MirrorKnownPairs();
        Graft.FileMissingSessionRecords(filingInto, ClaudeProcesses.IsRunning);
    }

    /// The profiles a sweep run from this launch may file into: the profile being
    /// opened, Claude's own, and the source it borrows from — the account that
    /// owns a recovered session is likely one of these.
    private static List<string> FilingProfiles(GraftConfig config)
    {
        var list = new List<string> { config.ProfileDir, GraftPaths.DefaultProfile };
        if (!string.IsNullOrEmpty(config.SourceDir)) list.Add(config.SourceDir!);
        return list;
    }

    private static void Launch(string profile)
    {
        var exe = ClaudeExe();
        if (exe is null) return;
        // A shortcut always carries its own profile; the default profile is the
        // one launched with no --user-data-dir at all.
        var args = Fs.SamePath(profile, GraftPaths.DefaultProfile)
            ? ""
            : $"--user-data-dir=\"{profile}\"";
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
        }
        catch { }
    }

    // MARK: - Reveal

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    /// Bring the Claude holding a profile back to the front. Its window is found
    /// by walking the process's own top-level windows rather than through
    /// <c>MainWindowHandle</c>: a Claude set to keep running in the tray answers a
    /// closed window by hiding it, not ending it, and a hidden window is no
    /// window at all as far as <c>MainWindowHandle</c> is concerned — which is
    /// how reopening a tray-resident profile from the menu did nothing. The HWND
    /// is still there, only hidden, so the walk still finds it and shows it.
    private static void Reveal(int pid)
    {
        try
        {
            var window = FindAppWindow((uint)pid);
            if (window == IntPtr.Zero) return;
            ShowWindow(window, SW_RESTORE);
            SetForegroundWindow(window);
        }
        catch { }
    }

    /// Electron's own top-level window is a Chromium widget host; every one of
    /// them, main window and popups alike, carries this class.
    private const string ElectronWindowClass = "Chrome_WidgetWin_1";

    /// The process's real window, hidden or shown. A title is not enough to pick
    /// it out: Chromium also owns an OleDdeWndClass window titled "DDE Server
    /// Window" for shell activation, and it can sit ahead of the real one in the
    /// enumeration — so matching the first titled window showed that instead, and
    /// SW_RESTORE dragged the hidden DDE window into the switcher beside Claude.
    /// The app window is the Chromium widget host, and its class is what says so;
    /// a title on top of that rules out the process's untitled background widgets.
    private static IntPtr FindAppWindow(uint pid)
    {
        var found = IntPtr.Zero;
        var className = new StringBuilder(64);
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner != pid || GetWindowTextLength(hWnd) == 0) return true;
            GetClassName(hWnd, className, className.Capacity);
            if (className.ToString() != ElectronWindowClass) return true;
            found = hWnd;
            return false;   // stop at the first real app window this process owns
        }, IntPtr.Zero);
        return found;
    }
}
