namespace ClaudeGraft.Core;

/// <summary>
/// Which Claude is running, and where. The Mac build reads <c>ps</c> and matches
/// on the app binary path; here the process is <c>claude.exe</c> and its command
/// line is read from WMI. The predicates that pick a profile out of a command
/// line are pure and tested; the process enumeration behind them is a hook, so
/// a test drives the predicates without any live processes.
/// </summary>
public static class ClaudeProcesses
{
    /// Returns every running <c>claude.exe</c> paired with its command line.
    /// Replaced in tests; defaults to the WMI query on Windows and to nothing
    /// anywhere else, so the cross-platform Core still compiles and a run off
    /// Windows reports no Claude rather than throwing.
    public static Func<IReadOnlyList<(int pid, string command)>> Enumerate = () =>
        OperatingSystem.IsWindows()
            ? WindowsProcessQuery.ClaudeProcesses()
            : Array.Empty<(int pid, string command)>();

    /// A helper process — a renderer, a GPU or utility process — rather than the
    /// browser process that owns the window. Electron marks these with --type;
    /// only the one without it answers to being shown.
    public static bool IsHelper(string command) =>
        command.Contains("--type=", StringComparison.Ordinal);

    public static bool HasUserDataDir(string command) =>
        command.Contains("--user-data-dir=", StringComparison.OrdinalIgnoreCase);

    /// The main binary of a Claude on no profile in particular — no helper flag,
    /// and no --user-data-dir, which is the only mark the default profile has.
    public static bool IsDefaultInstance(string command) =>
        !IsHelper(command) && !HasUserDataDir(command);

    /// Whether a command line carries this exact profile's --user-data-dir.
    ///
    /// Anchored at the end of the value: without that one profile's path is a
    /// prefix of another's — <c>…\Claude</c> sits inside <c>…\Claude-2</c> — and
    /// every shorter-named profile looks like it is running as soon as any
    /// longer-named one is. The value may be quoted when the path holds a space,
    /// so both spellings are checked.
    public static bool CarriesDataDir(string command, string profilePath)
    {
        // Unquoted: the value runs to a space or the end of the line.
        var bare = "--user-data-dir=" + profilePath;
        var i = 0;
        while ((i = command.IndexOf(bare, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var after = i + bare.Length;
            if (after == command.Length || command[after] == ' ' || command[after] == '"') return true;
            i = after;
        }
        // Quoted: --user-data-dir="C:\path with a space"
        return command.Contains("--user-data-dir=\"" + profilePath + "\"", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRunning(string profile) => IsRunning(profile, Enumerate());

    /// The pure form the tests drive: a yes or no against a list of command
    /// lines already in hand.
    public static bool IsRunning(string profile, IReadOnlyList<(int pid, string command)> processes)
    {
        // A yes-or-no counts any process, a helper included, the way pgrep -f
        // does on the Mac.
        if (Fs.SamePath(profile, GraftPaths.DefaultProfile))
            return processes.Any(p => IsDefaultInstance(p.command));
        return processes.Any(p => CarriesDataDir(p.command, profile));
    }

    public static int? ProcessIdentifier(string profile) => ProcessIdentifier(profile, Enumerate());

    /// The pid of the Claude holding this profile, if one holds it — the browser
    /// process alone, since every helper carries the same --user-data-dir and
    /// only the browser answers to being brought forward.
    public static int? ProcessIdentifier(string profile, IReadOnlyList<(int pid, string command)> processes)
    {
        if (Fs.SamePath(profile, GraftPaths.DefaultProfile))
            return processes.Where(p => IsDefaultInstance(p.command))
                            .Select(p => (int?)p.pid).FirstOrDefault();
        return processes.Where(p => !IsHelper(p.command) && CarriesDataDir(p.command, profile))
                        .Select(p => (int?)p.pid).FirstOrDefault();
    }
}
