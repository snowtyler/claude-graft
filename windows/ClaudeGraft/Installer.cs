using ClaudeGraft.Core;

namespace ClaudeGraft;

/// <summary>
/// Writes the desktop shortcut a profile is opened from. The Mac builds a small
/// .app bundle carrying a copy of the launcher; Windows writes a .lnk pointing
/// at the launcher stub installed beside the app, whose folder never moves.
///
/// Deliberately blind to anything this app did not create: a .lnk is only ever
/// removed or overwritten when it already points at our own launcher, so an
/// unrelated shortcut that happens to share a name is left alone — the Windows
/// echo of the graft.json check that guards every destructive step on the Mac.
/// </summary>
public static class Installer
{
    /// Names that belong to Claude itself and must never be written over.
    public static readonly string[] ReservedNames = { "Claude", "Claude Graft" };

    /// The stub beside the app once published, under launcher\ in a plain build,
    /// or the newest one in the repo's build output while debugging.
    private static readonly Lazy<string?> launcherExe = new(FindLauncher);
    internal static string? LauncherExe => launcherExe.Value;

    /// Where earlier versions copied the stub, and where their shortcuts point.
    private static string LegacyLauncherDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeGraft", "launcher");
    private static string LegacyLauncherExe => Path.Combine(LegacyLauncherDir, "GraftLaunch.exe");

    private static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static string LinkPath(string name) => Path.Combine(DesktopDir, name + ".lnk");

    public enum InstallError { ReservedName, NameTaken, MissingLauncher, WriteFailed }

    public sealed class InstallException(InstallError reason, string? detail = null) : Exception(detail ?? reason.ToString())
    {
        public InstallError Reason { get; } = reason;
    }

    /// Creates or updates the desktop shortcut for a profile.
    public static void Install(Shortcut shortcut)
    {
        if (ReservedNames.Contains(shortcut.Name.Trim().TrimEnd('.'), StringComparer.OrdinalIgnoreCase))
            throw new InstallException(InstallError.ReservedName, $"“{shortcut.Name}” is the name of Claude itself. Pick another.");

        if (LauncherExe is not { } launcher)
            throw new InstallException(InstallError.MissingLauncher, "This copy of Claude Graft is missing its launcher.");

        var path = LinkPath(shortcut.Name);
        // Something already there that is not ours must not be clobbered.
        if (File.Exists(path) && !IsOurs(path))
            throw new InstallException(InstallError.NameTaken, $"A shortcut named “{shortcut.Name}” already exists that Claude Graft did not create.");

        try
        {
            WriteLink(path, launcher, shortcut.Folder, ShortcutIcons.LinkIcon(shortcut),
                      $"Open {shortcut.Name} — a Claude Desktop profile");
            RefreshPinnedCopies(shortcut, launcher);
        }
        catch (Exception e)
        {
            throw new InstallException(InstallError.WriteFailed, e.Message);
        }
    }

    /// Removes a shortcut's .lnk, and a stale one left by a rename. Only ever a
    /// link that points at our launcher.
    public static void Uninstall(Shortcut shortcut) => RemoveLink(shortcut.Name);

    /// The .lnk at one name, if it is ours. A rename removes only the old name:
    /// the new one has just been written and is the shortcut's only way in.
    public static void RemoveLink(string name)
    {
        var path = LinkPath(name);
        if (File.Exists(path) && IsOurs(path)) try { File.Delete(path); } catch { }
    }

    /// The .lnk this app installed for a shortcut, or null if there is none of
    /// ours at that name.
    public static string? InstalledLink(Shortcut shortcut)
    {
        var path = LinkPath(shortcut.Name);
        return File.Exists(path) && IsOurs(path) ? path : null;
    }

    // MARK: - The launcher

    private static string? FindLauncher()
    {
        foreach (var exe in new[] {
            Path.Combine(AppContext.BaseDirectory, "GraftLaunch.exe"),
            Path.Combine(AppContext.BaseDirectory, "launcher", "GraftLaunch.exe") })
            if (File.Exists(exe)) return exe;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var built = Path.Combine(dir.FullName, "GraftLaunch", "bin");
            if (!Directory.Exists(built)) continue;
            var exe = Directory.EnumerateFiles(built, "GraftLaunch.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (exe is not null) return exe;
        }
        return null;
    }

    /// Moves shortcuts off the per-user copy of the launcher earlier versions
    /// kept, then puts a junction onto the current launcher's folder where that
    /// copy stood, so a .lnk this pass could not find — pinned somewhere else,
    /// or copied by hand — still opens its profile.
    public static void MigrateLegacyLauncher()
    {
        if (LauncherExe is not { } launcher) return;
        var current = Path.GetDirectoryName(launcher)!;
        var legacy = LegacyLauncherDir;

        if (Junction.IsLink(legacy))
        {
            // A dev build and an installed one take turns running here.
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Junction.Target(legacy) ?? ""),
                               Path.TrimEndingDirectorySeparator(current), StringComparison.OrdinalIgnoreCase))
            {
                Junction.Remove(legacy);
                Junction.Create(legacy, current);
            }
            return;
        }
        if (!Directory.Exists(legacy)) return;

        foreach (var link in LinksToRetarget())
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                var lnk = shell.CreateShortcut(link);
                if (!string.Equals((string)lnk.TargetPath, LegacyLauncherExe, StringComparison.OrdinalIgnoreCase)) continue;
                lnk.TargetPath = launcher;
                lnk.WorkingDirectory = current;
                lnk.Save();
            }
            catch { }
        }

        // A launcher or its Electron still running holds files open; the next
        // start tries again, and the shortcuts already point at the new stub.
        try { Directory.Delete(legacy, recursive: true); } catch { return; }
        Junction.Create(legacy, current);
    }

    /// A copy of the link pinned to the taskbar or Start menu is a separate
    /// file, and keeps the launcher and icon it was pinned with unless told.
    private static void RefreshPinnedCopies(Shortcut shortcut, string launcher)
    {
        var (file, index) = ShortcutIcons.LinkIcon(shortcut);
        var desktop = LinkPath(shortcut.Name);
        var rewritten = false;
        foreach (var path in LinksToRetarget())
        {
            if (string.Equals(path, desktop, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                var link = shell.CreateShortcut(path);
                if (!IsLauncher((string)link.TargetPath)
                    || !string.Equals(((string)link.Arguments).Trim().Trim('"'), shortcut.Folder, StringComparison.OrdinalIgnoreCase))
                    continue;
                link.TargetPath = launcher;
                link.WorkingDirectory = Path.GetDirectoryName(launcher);
                if (file.Length > 0) link.IconLocation = file + "," + index;
                link.Save();
                rewritten = true;
            }
            catch { }
        }
        // A taskbar pin is drawn from Explorer's icon cache, keyed by the pin
        // rather than the file, and only a system-wide notice redraws it.
        if (rewritten) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// Everywhere a .lnk to a profile is likely to be: the desktop this app
    /// writes to, and the Start menu and taskbar a person pins it to.
    private static IEnumerable<string> LinksToRetarget()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new[]
        {
            (DesktopDir, false),
            (Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs"), true),
            (Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch"), true),
        };
        foreach (var (root, recurse) in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = recurse,
                    IgnoreInaccessible = true,
                    // The taskbar keeps its pins under a hidden User Pinned folder.
                    AttributesToSkip = FileAttributes.None,
                }).ToList(); }
            catch { continue; }
            foreach (var link in links) yield return link;
        }
    }

    // MARK: - .lnk and icon

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    // Any copy of the launcher, not only this one: a link made by a development
    // build, or by an install since moved, is still Graft's to replace.
    private static bool IsLauncher(string target) =>
        string.Equals(Path.GetFileName(target), "GraftLaunch.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsOurs(string linkPath)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var link = shell.CreateShortcut(linkPath);
            return IsLauncher((string)link.TargetPath);
        }
        catch { return false; }
    }

    private static void WriteLink(string path, string target, string arguments, (string File, int Index) icon, string description)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.Arguments = arguments;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        if (icon.File.Length > 0) link.IconLocation = icon.File + "," + icon.Index;
        link.Description = description;
        link.Save();
    }
}
