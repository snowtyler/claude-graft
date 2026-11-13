namespace ClaudeGraft.Core;

public static class InstallFolder
{
    /// Every process this one starts inherits its working folder, and a Claude
    /// started by app activation cannot be given another; one holding Graft's
    /// install folder that way stops an update or a setup from replacing it.
    public static void Leave()
    {
        try { Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
