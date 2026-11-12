using ClaudeGraft.Core;
using Velopack;

namespace ClaudeGraft;

/// <summary>
/// Velopack's setup and Update.exe start this exe with arguments of their own and
/// wait for it to act and leave, so their hooks run before any of the app does.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => Installed())
            .OnBeforeUninstallFastCallback(_ => Uninstalling())
            .Run();

        // Two copies fight over the tray, the relay and every state file, so a second launch only asks the first to show itself.
        using var running = new Mutex(true, @"Local\ClaudeGraft.Running", out var first);
        if (!first)
        {
            ShowRequest.Set();
            return;
        }
        XamlGeneratedProgram.XamlGeneratedMain();
    }

    /// Set by a second launch; the running copy opens its window.
    public static readonly EventWaitHandle ShowRequest = new(false, EventResetMode.AutoReset, @"Local\ClaudeGraft.Show");

    // A machine coming from 1.x keeps the choice it made there; the move carries its link across.
    private static void Installed()
    {
        if (!LegacyInstall.Present) AutoStart.Set(true);
    }

    // What the 1.x uninstaller ran GraftLaunch --stop-sharing for, done in place.
    private static void Uninstalling()
    {
        try { ChimeHook.Remove(ChimeHook.SettingsFile); } catch { }
        try { PoolEnvironment.TakeOutLines(null, ClaudeProcesses.IsRunning); } catch { }
        AutoStart.Set(false);
        if (GraftUpdates.Root is { } root) try { LegacyInstall.RemoveJunctions(root); } catch { }
    }
}
