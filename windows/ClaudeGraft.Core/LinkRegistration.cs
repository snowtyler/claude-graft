using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeGraft.Core;

/// <summary>
/// Offers Graft to Windows as a handler for claude:// links. This only makes it
/// a candidate: Windows lets nothing make itself the default, so the person
/// picks it in Default apps. Claude's own registration is never touched.
/// </summary>
public static class LinkRegistration
{
    public const string ProgId = "ClaudeGraft.Link";
    public const string ApplicationName = "Claude Graft";
    private const string Capabilities = @"Software\ClaudeGraft\Capabilities";

    /// Written at install and again at every start, since an update moves the exe.
    public static void Write(RegistryKey currentUser, string exe)
    {
        using (var progId = currentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            progId.SetValue("", "Claude link");
            progId.SetValue("URL Protocol", "");
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            // Windows' picker names a handler by these before it falls back to the exe's own file name.
            using (var application = progId.CreateSubKey("Application"))
            {
                application.SetValue("ApplicationName", ApplicationName);
                application.SetValue("ApplicationIcon", $"\"{exe}\",0");
            }
            using var open = progId.CreateSubKey(@"shell\open");
            open.SetValue("FriendlyAppName", ApplicationName);
            using var command = open.CreateSubKey("command");
            command.SetValue("", $"\"{exe}\" --open-link \"%1\"");
        }
        using (var capabilities = currentUser.CreateSubKey(Capabilities))
        {
            capabilities.SetValue("ApplicationName", ApplicationName);
            capabilities.SetValue("ApplicationDescription", "Sends a Claude sign-in to the profile that started it.");
            using var urls = capabilities.CreateSubKey("URLAssociations");
            urls.SetValue("claude", ProgId);
        }
        using var registered = currentUser.CreateSubKey(@"Software\RegisteredApplications");
        registered.SetValue(ApplicationName, Capabilities);
    }

    /// Puts up Windows' own "Select a default app for CLAUDE links" picker, which
    /// sets the default without opening any link. False when it could not be shown.
    public static bool Choose(IntPtr owner)
    {
        var info = new OpenAsInfo
        {
            File = "claude",
            Flags = OaifUrlProtocol | OaifForceRegistration | OaifRegisterExt,
        };
        try { return SHOpenWithDialog(owner, ref info) >= 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private const int OaifRegisterExt = 0x2;
    private const int OaifForceRegistration = 0x20;
    private const int OaifUrlProtocol = 0x40;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public int Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr owner, ref OpenAsInfo info);

    public static void Remove(RegistryKey currentUser)
    {
        currentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, throwOnMissingSubKey: false);
        currentUser.DeleteSubKeyTree(Capabilities, throwOnMissingSubKey: false);
        using var registered = currentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
        registered?.DeleteValue(ApplicationName, throwOnMissingValue: false);
        using var own = currentUser.OpenSubKey(@"Software\ClaudeGraft");
        if (own is { SubKeyCount: 0, ValueCount: 0 }) currentUser.DeleteSubKey(@"Software\ClaudeGraft", throwOnMissingSubKey: false);
    }
}
