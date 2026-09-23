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
    private static string? LauncherExe => launcherExe.Value;

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
        if (ReservedNames.Contains(shortcut.Name))
            throw new InstallException(InstallError.ReservedName, $"“{shortcut.Name}” is the name of Claude itself. Pick another.");

        if (LauncherExe is not { } launcher)
            throw new InstallException(InstallError.MissingLauncher, "This copy of Claude Graft is missing its launcher.");

        var path = LinkPath(shortcut.Name);
        // Something already there that is not ours must not be clobbered.
        if (File.Exists(path) && !IsOurs(path))
            throw new InstallException(InstallError.NameTaken, $"A shortcut named “{shortcut.Name}” already exists that Claude Graft did not create.");

        try
        {
            WriteLink(path, launcher, shortcut.Folder, ClaudeIcon(),
                      $"Open {shortcut.Name} — a Claude Desktop profile");
        }
        catch (Exception e)
        {
            throw new InstallException(InstallError.WriteFailed, e.Message);
        }
    }

    /// Removes a shortcut's .lnk, and a stale one left by a rename. Only ever a
    /// link that points at our launcher.
    public static void Uninstall(Shortcut shortcut, string? previousName = null)
    {
        foreach (var name in new[] { shortcut.Name, previousName }.Where(n => !string.IsNullOrEmpty(n)))
        {
            var path = LinkPath(name!);
            if (File.Exists(path) && IsOurs(path)) try { File.Delete(path); } catch { }
        }
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
            try { links = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true }).ToList(); }
            catch { continue; }
            foreach (var link in links) yield return link;
        }
    }

    // MARK: - .lnk and icon

    /// Claude's own executable, so the shortcut wears Claude's icon in the Dock's
    /// Windows equivalent — the taskbar and Start menu.
    private static string ClaudeIcon() => Launcher.ClaudeExe() ?? "";

    private static bool IsOurs(string linkPath)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var link = shell.CreateShortcut(linkPath);
            string target = link.TargetPath;
            return string.Equals(target, LauncherExe, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, LegacyLauncherExe, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void WriteLink(string path, string target, string arguments, string icon, string description)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.Arguments = arguments;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        if (icon.Length > 0) link.IconLocation = icon + ",0";
        link.Description = description;
        link.Save();
    }
}
