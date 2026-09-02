using System.Text.Json;
using ClaudeGraft.Core;

namespace ClaudeGraft;

/// <summary>
/// The list of shortcuts this app keeps, in ClaudeGraft/shortcuts.json — the one
/// state file other processes read, so every write is atomic: a launcher reads
/// it to learn which profiles are this app's doing, and a plain write truncates
/// before it fills.
/// </summary>
public sealed class ShortcutStore
{
    public List<Shortcut> Shortcuts { get; private set; } = new();

    private static string File => Path.Combine(GraftPaths.OwnData, "shortcuts.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public ShortcutStore() => Load();

    public void Load()
    {
        try
        {
            Shortcuts = JsonSerializer.Deserialize<List<Shortcut>>(
                System.IO.File.ReadAllBytes(File), Options) ?? new();
        }
        catch { Shortcuts = new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            AtomicWrite.Bytes(File, JsonSerializer.SerializeToUtf8Bytes(Shortcuts, Options));
        }
        catch { }
    }

    public Shortcut? Get(Guid id) => Shortcuts.FirstOrDefault(s => s.Id == id);

    /// Where a shortcut actually reads its chats from, following one hop.
    public string? SourceDir(Shortcut shortcut) => shortcut.Source.Kind switch
    {
        SourceKind.Own => null,
        SourceKind.Main => GraftPaths.DefaultProfile,
        SourceKind.Shortcut => shortcut.Source.ShortcutId is Guid id ? Get(id)?.ProfileDir : null,
        _ => null,
    };

    /// The profile whose chat store a shortcut ends up reading, following the
    /// chain of sources to its end.
    public string ChatRoot(Shortcut shortcut)
    {
        var seen = new HashSet<Guid>();
        var current = shortcut;
        while (true)
        {
            switch (current.Source.Kind)
            {
                case SourceKind.Own: return current.ProfileDir;
                case SourceKind.Main: return GraftPaths.DefaultProfile;
                case SourceKind.Shortcut:
                    if (current.Source.ShortcutId is not Guid id || !seen.Add(current.Id) || Get(id) is not Shortcut next)
                        return current.ProfileDir;
                    current = next;
                    break;
            }
        }
    }

    public string Label(ShortcutSource source) => source.Kind switch
    {
        SourceKind.Own => "Its own chats",
        SourceKind.Main => "Main Claude",
        SourceKind.Shortcut => source.ShortcutId is Guid id ? Get(id)?.Name ?? "Removed shortcut" : "Removed shortcut",
        _ => "Main Claude",
    };

    /// Numbering starts at two, since the stock app is the first one.
    public string UniqueName(string @base = "Claude")
    {
        var n = 2;
        var candidate = $"{@base} {n}";
        while (Shortcuts.Any(s => s.Name == candidate)) candidate = $"{@base} {++n}";
        return candidate;
    }

    /// The GraftConfig a shortcut launches with — profile plus resolved source.
    public GraftConfig ConfigFor(Shortcut shortcut) => new()
    {
        ProfileDir = shortcut.ProfileDir,
        SourceDir = SourceDir(shortcut),
    };
}
