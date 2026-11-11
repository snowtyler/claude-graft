using ClaudeGraft.Core;

namespace ClaudeGraft;

/// <summary>
/// Every signed-in account's artifacts in one list, and the groups they are
/// shared with. Lives on the UI thread; Changed is raised there.
/// </summary>
public sealed class ArtifactCenter
{
    public enum AccountState { Unknown, Loading, Ready, SignedOut, Failed }

    private static ArtifactCenter? _shared;
    public static ArtifactCenter Shared => _shared ??= new ArtifactCenter();

    public event Action? Changed;

    public List<ArtifactAccount> Accounts { get; private set; } = [];
    public List<Artifact> Artifacts { get; private set; } = [];
    public List<ArtifactGroup> Groups { get; private set; } = [];
    public string? Status { get; private set; }
    /// The last sign-in to finish, from whichever pane, so every pane can ask
    /// for that account at once.
    public (Guid Account, DateTimeOffset At)? LastSignIn { get; private set; }

    private readonly Dictionary<Guid, AccountState> _states = new();
    private readonly Dictionary<Guid, ClaudeWebSession> _sessions = new();
    private readonly Dictionary<Guid, List<Artifact>> _lists = new();
    /// Which load of each account is the latest, so one a sign-in has since
    /// superseded writes nothing over the one that followed it.
    private readonly Dictionary<Guid, int> _generations = new();
    private ArtifactSignInWindow? _signIn;
    /// False only when an unreadable file could not be set aside either, so
    /// saving would write over it.
    private bool _mayWriteAccounts = true, _mayWriteGroups = true;

    private ArtifactCenter()
    {
        var accountsWereRead = false;
        var accounts = ArtifactFiles.Read<List<ArtifactAccount>>(ArtifactFiles.Accounts);
        switch (accounts.State)
        {
            case ArtifactFiles.State.Read: Accounts = accounts.Value!; accountsWereRead = true; break;
            case ArtifactFiles.State.Unreadable: _mayWriteAccounts = SetAside(ArtifactFiles.Accounts); break;
        }
        var groups = ArtifactFiles.ReadEach<ArtifactGroup>(ArtifactFiles.Groups);
        switch (groups.State)
        {
            case ArtifactFiles.State.Read:
                Groups = groups.Value.Values;
                // The whole file goes aside before the groups that did read are
                // written back, so the one that did not is still on disk.
                if (groups.Value.Lost > 0)
                {
                    _mayWriteGroups = SetAside(ArtifactFiles.Groups);
                    if (_mayWriteGroups) ArtifactFiles.Save(Groups, ArtifactFiles.Groups);
                }
                break;
            case ArtifactFiles.State.Unreadable: _mayWriteGroups = SetAside(ArtifactFiles.Groups); break;
        }
        // Only against a list that was read: one set aside, or never written,
        // may name the accounts these folders belong to.
        if (accountsWereRead) _ = RemoveStrayStores();
    }

    private async Task RemoveStrayStores()
    {
        foreach (var id in ClaudeWebSession.StoreIdentifiers())
        {
            if (Account(id) is not null || _sessions.ContainsKey(id) || _signIn?.Account == id) continue;
            Diagnostics.Note("artifacts.store stray", new Dictionary<string, object?> { ["store"] = id.ToString()[..8] });
            await ClaudeWebSession.Forget(id);
        }
    }

    private static bool SetAside(string file)
    {
        var moved = ArtifactFiles.SetAside(file);
        Diagnostics.Note("artifacts.file unreadable", new Dictionary<string, object?> { ["file"] = Path.GetFileName(file), ["setAside"] = moved is null ? "could not move" : Path.GetFileName(moved) });
        return moved is not null;
    }

    private void SaveAccounts()
    {
        if (_mayWriteAccounts) ArtifactFiles.Save(Accounts, ArtifactFiles.Accounts);
    }

    public void SaveGroups(List<ArtifactGroup> groups)
    {
        Groups = groups;
        if (_mayWriteGroups) ArtifactFiles.Save(Groups, ArtifactFiles.Groups);
        Changed?.Invoke();
    }

    public AccountState StateOf(Guid account) => _states.GetValueOrDefault(account);
    public ArtifactAccount? Account(Guid id) => Accounts.FirstOrDefault(a => a.Id == id);
    public string NameOf(Guid id) => Account(id)?.Email ?? "Signing in…";
    public bool AnyLoading => _states.Values.Contains(AccountState.Loading);
    public void ClearStatus() { Status = null; Changed?.Invoke(); }

    /// What another pane learned about a sign-in, so every pane says the same.
    public void NoteSignIn(Guid account, bool accepted)
    {
        if (Account(account) is null || StateOf(account) == AccountState.Loading) return;
        _states[account] = accepted ? AccountState.Ready : AccountState.SignedOut;
        Changed?.Invoke();
    }

    public void SignInNewAccount() => SignIn(Guid.NewGuid());

    public void SignInAgain(Guid id)
    {
        if (Account(id) is not null) SignIn(id);
    }

    private void SignIn(Guid id)
    {
        _signIn?.Close();
        ArtifactSignInWindow? window = null;
        window = new ArtifactSignInWindow(id, identity => SignedIn(id, identity), () =>
        {
            if (_signIn != window) return;
            _signIn = null;
            // A folder made for a new account that never finished signing in
            // holds nothing worth keeping.
            if (Account(id) is null && !_sessions.ContainsKey(id)) _ = ClaudeWebSession.Forget(id);
        });
        _signIn = window;
        window.Show();
    }

    private void SignedIn(Guid id, ArtifactAPI.Identity identity)
    {
        _signIn = null;
        var wasAnother = Account(id) is { } before && before.AccountUuid != identity.AccountUuid;
        var (accounts, retired) = ArtifactAccounts.SignedIn(Accounts, id, identity);
        Accounts = accounts;
        SaveAccounts();
        foreach (var earlier in retired) Retire(earlier);
        if (wasAnother) _lists.Remove(id);
        if (retired.Count > 0) Status = $"Signed in to {identity.Email} again";
        _sessions.GetValueOrDefault(id)?.Reset();
        LastSignIn = (id, DateTimeOffset.UtcNow);
        Changed?.Invoke();
        _ = Load(id, again: true);
    }

    public void SignOut(Guid id)
    {
        if (_signIn?.Account == id)
        {
            _signIn.Close();
            _signIn = null;
        }
        Accounts = Accounts.Where(a => a.Id != id).ToList();
        SaveAccounts();
        Retire(id);
        Artifacts = ArtifactAPI.Merge(Accounts.Where(a => _lists.ContainsKey(a.Id)).Select(a => _lists[a.Id]));
        Changed?.Invoke();
    }

    /// Lets go of every page on an account's folder, then removes the folder,
    /// which WebView2 holds open while any page on it is alive.
    private void Retire(Guid id)
    {
        _sessions.Remove(id, out var session);
        session?.Close();
        _lists.Remove(id);
        _states.Remove(id);
        _generations.Remove(id);
        _ = RemoveWhenReleased(session, id);
    }

    private static async Task RemoveWhenReleased(ClaudeWebSession? session, Guid id)
    {
        if (session is not null) await session.Released();
        await ClaudeWebSession.Forget(id);
    }

    /// When the pane is shown and from its refresh button, never on a timer:
    /// each pass is several requests per account to a service that did not ask.
    public void Refresh()
    {
        foreach (var account in Accounts.ToList()) _ = Load(account.Id);
    }

    /// The organization an account's things live under, asked once and kept.
    public async Task<string> OrganizationOf(Guid id)
    {
        var account = Account(id) ?? new ArtifactAccount(id, null, null, null);
        if (account.Org is null || account.Email is null)
        {
            var identity = await Session(id).Identity();
            account = account with { Email = identity.Email, AccountUuid = identity.AccountUuid, Org = identity.Org };
            var index = Accounts.FindIndex(a => a.Id == id);
            if (index >= 0)
            {
                Accounts[index] = account;
                SaveAccounts();
            }
        }
        return account.Org ?? throw new ClaudeWebSession.WebFailure(ClaudeWebSession.Failure.Unreadable);
    }

    /// Shared with Projects and Search later. An account no longer listed gets
    /// one that answers signed out and loads nothing, so a request still
    /// running after a sign-out cannot build a page on a folder being removed.
    public ClaudeWebSession Session(Guid id)
    {
        if (_sessions.TryGetValue(id, out var existing)) return existing;
        var session = new ClaudeWebSession(id);
        if (Account(id) is null)
        {
            session.Close();
            return session;
        }
        _sessions[id] = session;
        return session;
    }

    private async Task Load(Guid id, bool again = false)
    {
        if (Account(id) is null || (!again && StateOf(id) == AccountState.Loading)) return;
        var generation = _generations.GetValueOrDefault(id) + 1;
        _generations[id] = generation;
        _states[id] = AccountState.Loading;
        Changed?.Invoke();
        var session = Session(id);
        List<Artifact>? found = null;
        Exception? failure = null;
        try
        {
            var org = await OrganizationOf(id);
            found = [];
            foreach (var rel in new[] { "mine", "shared" })
            {
                string? token = null;
                for (var i = 0; i < ArtifactAPI.MaximumPages; i++)
                {
                    var (status, body) = await session.Send("GET", ArtifactAPI.ListPath(rel, org, token));
                    if (status != 200 || ArtifactAPI.Page(body, id, org) is not { } page)
                        throw new ClaudeWebSession.WebFailure(ClaudeWebSession.Failure.Unreadable);
                    found.AddRange(page.Artifacts);
                    token = page.Next;
                    if (token is null) break;
                }
            }
        }
        catch (Exception e) { failure = e; }
        // A newer load owns what is shown now, and an account signed out while
        // this ran owns nothing.
        if (_generations.GetValueOrDefault(id) != generation || Account(id) is null) return;
        if (failure is null)
        {
            _lists[id] = found!;
            _states[id] = AccountState.Ready;
        }
        else if (failure is ClaudeWebSession.WebFailure { Reason: ClaudeWebSession.Failure.SignedOut })
        {
            _lists.Remove(id);
            _states[id] = AccountState.SignedOut;
        }
        else
        {
            _states[id] = AccountState.Failed;
            Diagnostics.Note("artifacts.list failed", new Dictionary<string, object?> { ["reason"] = failure.Message });
        }
        Artifacts = ArtifactAPI.Merge(Accounts.Where(a => _lists.ContainsKey(a.Id)).Select(a => _lists[a.Id]));
        Changed?.Invoke();
    }
}
