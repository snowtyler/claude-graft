using ClaudeGraft.Core;

namespace ClaudeGraft;

/// <summary>
/// Every signed-in account's projects in one list. The accounts are the
/// Artifacts pane's, in the same browser stores and asked from the same pages;
/// nothing here signs in, so an account added or signed out in either pane is
/// added or gone in both. Lives on the UI thread.
/// </summary>
public sealed class ProjectCenter
{
    public enum State { Unknown, Loading, Ready, SignedOut, Failed }

    private static ProjectCenter? _shared;
    public static ProjectCenter Shared => _shared ??= new ProjectCenter(ArtifactCenter.Shared);

    public event Action? Changed;
    public ArtifactCenter Accounts { get; }
    public List<ClaudeProject> Projects { get; private set; } = [];
    public Dictionary<Guid, State> ProjectStates { get; } = new();

    private readonly Dictionary<Guid, List<ClaudeProject>> _lists = new();
    private HashSet<Guid> _known;
    private (Guid, DateTimeOffset)? _lastSignIn;
    /// Whether the pane has asked yet: a sign-in made before it was ever shown
    /// waits for it, like every other account.
    private bool _asked;

    private ProjectCenter(ArtifactCenter accounts)
    {
        Accounts = accounts;
        _known = accounts.Accounts.Select(a => a.Id).ToHashSet();
        _lastSignIn = accounts.LastSignIn;
        accounts.Changed += AccountsChanged;
    }

    public bool Loading => ProjectStates.Values.Contains(State.Loading);

    /// The accounts claude.ai would not answer for, by name, for the line under
    /// the list.
    public List<string> Trouble() => Accounts.Accounts.Select(a => ProjectStates.GetValueOrDefault(a.Id) switch
    {
        State.SignedOut => $"Sign in to {Accounts.NameOf(a.Id)} again",
        State.Failed => $"{Accounts.NameOf(a.Id)} didn't answer",
        _ => null,
    }).OfType<string>().ToList();

    private void AccountsChanged()
    {
        var ids = Accounts.Accounts.Select(a => a.Id).ToHashSet();
        if (!ids.SetEquals(_known))
        {
            // An account signed out anywhere leaves this list with it.
            _known = ids;
            foreach (var gone in _lists.Keys.Where(k => !ids.Contains(k)).ToList()) _lists.Remove(gone);
            foreach (var gone in ProjectStates.Keys.Where(k => !ids.Contains(k)).ToList()) ProjectStates.Remove(gone);
            Merge();
        }
        // A sign-in from either pane is asked for here too, since a person just
        // asked for that account.
        if (Accounts.LastSignIn is { } signIn && signIn != _lastSignIn)
        {
            _lastSignIn = signIn;
            ProjectStates.Remove(signIn.Account);
            if (_asked) _ = Load(signIn.Account);
        }
        Changed?.Invoke();
    }

    /// When the pane is shown and from its refresh button, never on a timer.
    public void Refresh()
    {
        _asked = true;
        foreach (var account in Accounts.Accounts.ToList()) _ = Load(account.Id);
    }

    private async Task Load(Guid id)
    {
        if (Accounts.Account(id) is null || ProjectStates.GetValueOrDefault(id) == State.Loading) return;
        ProjectStates[id] = State.Loading;
        Changed?.Invoke();
        try
        {
            var org = await Accounts.OrganizationOf(id);
            var session = Accounts.Session(id);
            var found = new List<ClaudeProject>();
            for (var page = 0; page < ClaudeAPI.MaximumProjectPages; page++)
            {
                var (status, body) = await session.Send("GET", ClaudeAPI.ProjectsPath(org, page * ClaudeAPI.ProjectPageSize));
                if (status != 200 || ClaudeAPI.Projects(body, id) is not { } list)
                    throw new ClaudeWebSession.WebFailure(ClaudeWebSession.Failure.Unreadable);
                // The page itself asks no offset, so one that is ignored answers
                // the first page again; a page with nothing new is the end.
                var fresh = list.Projects.Where(p => found.All(f => f.Id != p.Id)).ToList();
                found.AddRange(fresh);
                if (!list.HasMore || fresh.Count == 0) break;
            }
            // An account without the Beta, or a Beta list that will not answer,
            // still has its classic projects listed.
            var (channelStatus, channels) = await session.Send("GET", ClaudeAPI.ChannelsPath, extraHeaders: ClaudeAPI.CodeHeaders(org));
            if (channelStatus == 200 && ClaudeAPI.Channels(channels, id) is { } beta) found.AddRange(beta);
            else Diagnostics.Note("projects.beta refused", new Dictionary<string, object?> { ["status"] = channelStatus });
            // Signed out while this ran: what it found goes with the account.
            if (Accounts.Account(id) is null) return;
            _lists[id] = found;
            ProjectStates[id] = State.Ready;
            Accounts.NoteSignIn(id, accepted: true);
        }
        catch (ClaudeWebSession.WebFailure e) when (e.Reason == ClaudeWebSession.Failure.SignedOut)
        {
            if (Accounts.Account(id) is null) return;
            _lists.Remove(id);
            ProjectStates[id] = State.SignedOut;
            Accounts.NoteSignIn(id, accepted: false);
        }
        catch (Exception e) when (e is ClaudeWebSession.WebFailure or System.Text.Json.JsonException)
        {
            if (Accounts.Account(id) is null) return;
            ProjectStates[id] = State.Failed;
            Diagnostics.Note("projects.list failed", new Dictionary<string, object?> { ["reason"] = e.Message });
        }
        finally
        {
            Merge();
            Changed?.Invoke();
        }
    }

    private void Merge() =>
        Projects = ClaudeAPI.Merge(Accounts.Accounts.Where(a => _lists.ContainsKey(a.Id)).Select(a => _lists[a.Id]));
}
