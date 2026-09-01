using System.Runtime.InteropServices;

namespace ClaudeGraft.Core;

/// <summary>
/// A directory junction is the Windows stand-in for the symlink the mac build
/// grafts with. Measured on this machine against every property the graft
/// depends on: created without administrator rights, a read through it sees the
/// target, a write through it lands in the target, and dropping it leaves the
/// target untouched — the reversibility the whole app rests on.
///
/// The one rule the rest of the app is built on is <see cref="ResolvesInside"/>:
/// a real folder is one a profile will write a record into, a link is not.
/// mac asks whether a path is a symlink; here the equivalent is a reparse
/// point whose target climbs out of the profile.
/// </summary>
public static class Junction
{
    /// Create <paramref name="linkPath"/> as a junction onto <paramref name="target"/>.
    /// Both the mac symlink and this take the link ahead of the target, and the
    /// graft is only ever made with the profile's own folder as the link.
    public static void Create(string linkPath, string target)
    {
        // .NET creates a symbolic link here, which on a default Windows install
        // needs either administrator rights or Developer Mode; a junction needs
        // neither, which is why Claude Desktop shortcuts can be grafted by an
        // ordinary user. mklink /J is the one primitive that makes a junction
        // without a P/Invoke into DeviceIoControl.
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{linkPath}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(start)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new IOException(
                $"could not create junction {linkPath} -> {target}: {p.StandardError.ReadToEnd().Trim()}");
    }

    /// A junction, or any reparse point, carries this attribute. The mac build's
    /// isSymlink check answers here.
    public static bool IsLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// Where a junction points, or null for a path that is not one. .NET reads
    /// the reparse target directly; the value is what tells a grafted folder
    /// from one the profile owns.
    public static string? Target(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Attributes.HasFlag(FileAttributes.ReparsePoint)
            ? info.LinkTarget
            : null;
    }

    /// The rule the session sweep exists to work around: a profile will not
    /// write a record into a folder that resolves outside itself. A real
    /// directory the profile owns resolves inside <paramref name="profile"/>;
    /// a graft junction resolves out to the source. Asked of the folder that a
    /// record would land in, never of the account it is stamped with.
    public static bool ResolvesInside(string path, string profile)
    {
        var resolved = new DirectoryInfo(path);
        // A reparse point resolves to its target; a plain folder resolves to
        // itself. Either way the question is whether the destination sits under
        // the profile root once every link on the way is followed.
        var full = Path.GetFullPath(resolved.LinkTarget ?? resolved.FullName);
        var root = Path.GetFullPath(profile);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// Drop the link without following it into the target. Directory.Delete on a
    /// junction removes the link alone — measured leaving every file in the
    /// target where it was, which is what makes going back a handover rather
    /// than a delete.
    public static void Remove(string linkPath)
    {
        if (IsLink(linkPath))
            Directory.Delete(linkPath);
    }
}
