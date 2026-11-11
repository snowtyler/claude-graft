using ClaudeGraft.Core;
using Xunit;

namespace ClaudeGraft.Tests;

[Collection("GlobalState")]
public sealed class WindowsSafetyTests : IDisposable
{
    private readonly TempDir _temp = new();
    public WindowsSafetyTests()
    {
        GraftPaths.ProfilesRootOverride = _temp.Dir("profiles");
        GraftPaths.ClaudeProjectsOverride = _temp.Dir("transcripts");
    }
    public void Dispose()
    {
        GraftPaths.ProfilesRootOverride = null;
        GraftPaths.ClaudeProjectsOverride = null;
        _temp.Dispose();
    }

    [Theory(DisplayName = "Windows aliases, device names and reserved characters cannot become profile folders")]
    [InlineData("claude")][InlineData("CLAUDE")][InlineData("claudegraft")]
    [InlineData("Claude.")][InlineData("work.")]
    [InlineData("NUL")][InlineData("nul.json")][InlineData("COM1")][InlineData("LPT9.txt")]
    [InlineData("CONIN$")][InlineData("a*b")][InlineData("a?b")]
    [InlineData("a|b")][InlineData("a<b")][InlineData("a>b")][InlineData("a\"b")]
    public void InvalidNames(string value) => Assert.NotNull(Graft.ValidateFolder(value));

    [Fact(DisplayName = "a profile whose name is a prefix of another's does not contain it")]
    public void SiblingIsOutside()
    {
        var one = _temp.Dir("Claude");
        var other = _temp.Dir("Claude-Work", "store");
        Assert.False(Junction.ResolvesInside(other, one));
    }

    [Fact(DisplayName = "a real folder reached through a linked store resolves outside the profile")]
    public void LinkedAncestor()
    {
        _temp.Dir("source", "account", "org");
        var profile = _temp.Dir("profile");
        var store = Path.Combine(profile, "store");
        Junction.Create(store, _temp.Dir("source"));
        var through = Path.Combine(store, "account", "org");
        Assert.False(Junction.ResolvesInside(through, profile));
    }

    [Fact(DisplayName = "a junction path holding percent signs is taken literally")]
    public void LiteralJunctionPaths()
    {
        var source = _temp.Dir("source %TEMP% & data");
        File.WriteAllText(Path.Combine(source, "keep.txt"), "keep");
        var link = Path.Combine(_temp.Path, "link %TEMP% & data");
        Junction.Create(link, source);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(link, "keep.txt")));
    }

    [Fact(DisplayName = "a junction whose target has gone is still a link that can be removed")]
    public void DanglingJunction()
    {
        var source = _temp.Dir("vanishing");
        var link = Path.Combine(_temp.Path, "dangling");
        Junction.Create(link, source);
        Directory.Delete(source);
        Assert.True(Junction.IsLink(link));
        Junction.Remove(link);
        Assert.False(Fs.Exists(link));
    }

    [Fact(DisplayName = "a record another process holds open never costs the other side its copy")]
    public void LockedRecord()
    {
        var one = _temp.Dir("one");
        var other = _temp.Dir("other");
        foreach (var name in new[] { "local_locked.json", "local_other.json" })
        {
            File.WriteAllText(Path.Combine(one, name), "{}");
            File.WriteAllText(Path.Combine(other, name), "{}");
        }
        Graft.MirrorChatFolders(one, other);
        using (File.Open(Path.Combine(one, "local_locked.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Graft.MirrorChatFolders(one, other);
            Graft.MirrorChatFolders(one, other);
            Assert.True(File.Exists(Path.Combine(other, "local_locked.json")));
        }
    }

    [Fact(DisplayName = "a mirror state that will not parse is unreadable, never a first pass")]
    public void CorruptMirrorStateNotEmpty()
    {
        var state = Path.Combine(GraftPaths.OwnData, "mirrored-chats.json");
        Directory.CreateDirectory(GraftPaths.OwnData);
        File.WriteAllText(state, "{broken");
        Assert.ThrowsAny<Exception>(() => MirrorState.Load(state));
    }

    [Fact(DisplayName = "deleting a profile that is a junction leaves its target alone")]
    public void LinkedProfileDeletion()
    {
        var target = _temp.Dir("outside");
        File.WriteAllText(Path.Combine(target, "keep"), "keep");
        var link = GraftPaths.Profile("linked");
        Junction.Create(link, target);
        try { Graft.DeleteProfile(link, _ => false); } catch (ProfileException) { }
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep")));
    }

    [Fact(DisplayName = "an unreadable shortcut list is never saved over with an empty one")]
    public void CorruptShortcutState()
    {
        Directory.CreateDirectory(GraftPaths.OwnData);
        var path = Path.Combine(GraftPaths.OwnData, "shortcuts.json");
        File.WriteAllText(path, "{broken");
        var store = new ShortcutStore();
        try { store.Save(); } catch { }
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact(DisplayName = "the Claude Code copy inside a profile is not the desktop app")]
    public void StoreProcesses()
    {
        Assert.False(ClaudeProcesses.IsClaudeDesktop("\"C:\\profiles\\Claude-Work\\claude-code\\1.0\\claude.exe\""));
    }

    [Theory(DisplayName = "each way of spelling the data directory on a command line is recognised")]
    [InlineData("--user-data-dir=\"C:\\profiles\\Work account\"")]
    [InlineData("\"--user-data-dir=C:\\profiles\\Work account\"")]
    [InlineData("--user-data-dir \"C:\\profiles\\Work account\"")]
    public void ProfileArguments(string flags) =>
        Assert.True(ClaudeProcesses.CarriesDataDir("\"C:\\AnthropicClaude\\app-1.0\\claude.exe\" " + flags, @"C:\profiles\Work account"));

    [Fact(DisplayName = "a data-directory flag spelled inside another argument is not one")]
    public void MisleadingArgument() =>
        Assert.False(ClaudeProcesses.CarriesDataDir("claude.exe --other=--user-data-dir=C:\\profiles\\work", @"C:\profiles\work"));

    [Theory(DisplayName = "an unreadable session state is not an empty one")]
    [InlineData("{broken")][InlineData("null")]
    public void UnreadableSessionState(string content)
    {
        var path = _temp.Write("session-state.json", content);
        Assert.ThrowsAny<Exception>(() => SessionRecordState.Load(path));
    }

    [Fact(DisplayName = "a scoped Fable limit is read apart from the weekly one")]
    public void FableLimit()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
        {"five_hour":{"utilization":12},"seven_day":{"utilization":34},
         "limits":[{"kind":"weekly_scoped","scope":{"model":{"display_name":"Fable"}},"percent":56,"resets_at":"2026-10-01T00:00:00Z"}]}
        """);
        Assert.Equal(56, UsageApi.ReadingFrom(json.RootElement)!.Fable);
    }

    [Theory(DisplayName = "a record that will not read does not withdraw its session on repeated sweeps")]
    [InlineData("{broken")][InlineData("{}")][InlineData("{\"cliSessionId\":42}")]
    public void UnreadableRecordDoesNotWithdraw(string content)
    {
        var profile = GraftPaths.Profile("Work");
        var org = Path.Combine(profile, "claude-code-sessions", "account", "org");
        Directory.CreateDirectory(org);
        File.WriteAllText(Path.Combine(org, "local_record.json"), content);
        var stateFile = Path.Combine(GraftPaths.OwnData, "session-records.json");
        new SessionRecordState { Records = new() { ["remembered"] = Fs.Resolve(org) } }.Save(stateFile);
        Graft.FileMissingSessionRecords(new[] { profile }, () => false);
        Graft.FileMissingSessionRecords(new[] { profile }, () => false);
        var state = SessionRecordState.Load(stateFile);
        Assert.Empty(state.Withdrawn);
        Assert.Contains("remembered", state.Records.Keys);
    }

    [Fact(DisplayName = "not knowing which Claudes are running counts as one running")]
    public void UnknownProcessesCountAsRunning()
    {
        var saved = ClaudeProcesses.Enumerate;
        ClaudeProcesses.Enumerate = () => throw new IOException("fixture refusal");
        try { Assert.True(ClaudeProcesses.AnyDesktopRunning()); }
        finally { ClaudeProcesses.Enumerate = saved; }
    }

    [Fact(DisplayName = "the state lock can be taken again by the thread already holding it")]
    public void StateLockIsReentrant()
    {
        Assert.Equal(3, StateLock.Run(() => StateLock.Run(() => 3)));
    }

    [Fact(DisplayName = "a pass waits for another process holding the state lock")]
    public void StateLockSerializesThreads()
    {
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => StateLock.Run(() => { inside.Set(); release.Wait(); }));
        inside.Wait();
        var second = Task.Run(() => StateLock.Run(() => 1));
        Assert.False(second.Wait(200));
        release.Set();
        Assert.Equal(1, second.Result);
        holder.Wait();
    }
}
