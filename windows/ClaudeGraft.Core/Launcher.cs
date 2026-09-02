using System.Diagnostics;
using System.Runtime.InteropServices;

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
        if (ClaudeProcesses.IsRunning(config.ProfileDir))
        {
            if (ClaudeProcesses.ProcessIdentifier(config.ProfileDir) is int pid) Reveal(pid);
            return;
        }
        Graft.Apply(config);
        Launch(config.ProfileDir);
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

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    private static void Reveal(int pid)
    {
        try
        {
            var handle = Process.GetProcessById(pid).MainWindowHandle;
            if (handle == IntPtr.Zero) return;
            ShowWindow(handle, SW_RESTORE);
            SetForegroundWindow(handle);
        }
        catch { }
    }
}
