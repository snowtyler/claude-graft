namespace ClaudeGraft.Core;

/// <summary>
/// Where Claude Desktop keeps a profile on Windows. The mac build reads these
/// out of <c>~/Library/Application Support/Claude</c>; on Windows the same
/// store, folder for folder, lives under <c>%APPDATA%\Claude</c> — verified on
/// a real install carrying the same <c>config.json</c>, <c>claude-code-sessions</c>,
/// <c>local-agent-mode-sessions</c> and <c>plan-usage-history.json</c> the mac
/// layout has.
/// </summary>
public static class GraftPaths
{
    /// A profile is identified by the folder its user-data-dir points at. The
    /// default profile — the one Claude opens with no --user-data-dir — is the
    /// one under Roaming, and its absence of a flag is the only thing that
    /// marks it out, exactly as on mac.
    public static string DefaultProfile =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude");

    /// Chat history is <store>/<accountUuid>/<orgUuid>/ in both of these, the
    /// same nesting the mac build depends on. A profile reads only the account
    /// it is signed into, so a graft is made one level deep — mapping this
    /// profile's <account>/<org> onto the source's active one.
    public static readonly string[] ChatStoreNames =
    {
        "claude-code-sessions",
        "local-agent-mode-sessions",
    };

    /// Sits beside the account directories keyed by organization, not account,
    /// so a walk that takes it for an account reports its organization folders
    /// as chat stores. Named here so both the sweep and the state report skip
    /// it, as on mac.
    public static readonly string[] NonAccountStoreItems =
    {
        "skills-plugin",
    };

    public static IEnumerable<string> ChatStores(string profile) =>
        ChatStoreNames.Select(name => Path.Combine(profile, name));
}
