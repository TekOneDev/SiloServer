using System.Security.Principal;

namespace Silo.Plugins.Vault;

/// <summary>Turns a Windows account name ("DOMAIN\name", "name@domain") or a SID string into a SID string.</summary>
public interface ISidResolver
{
    string? Sid(string account);
}

/// <summary>Asks Windows (the domain) for SIDs. Results, including misses, are cached for the plugin's lifetime.</summary>
public sealed class WindowsSidResolver : ISidResolver
{
    readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string? Sid(string account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        account = account.Trim();
        if (account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return account.ToUpperInvariant();
        lock (_cache)
        {
            if (_cache.TryGetValue(account, out var hit)) return hit;
            string? sid = null;
            if (OperatingSystem.IsWindows())
            {
                try { sid = ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value; }
                catch (Exception e) when (e is IdentityNotMappedException or SystemException) { sid = null; }
            }
            return _cache[account] = sid;
        }
    }
}

/// <summary>How Vault accounts become Windows accounts, and what to do with security Silo cannot fully read.</summary>
public sealed class AclOptions
{
    /// <summary>Vault's permission id for Read (AccessPermis.Id). 1 on every Vault seen so far.</summary>
    public long ReadPermissionId { get; set; } = 1;
    /// <summary>Domain for Vault users imported from Active Directory whose names carry no domain. Default: the server's domain.</summary>
    public string? Domain { get; set; }
    /// <summary>Vault user name → Windows account, for Vault-only accounts (no AD).</summary>
    public Dictionary<string, string> UserMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Vault group name → Windows group or SID.</summary>
    public Dictionary<string, string> GroupMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Also treat Vault-only accounts as Windows accounts of the same name in <see cref="Domain"/>. Off by default.</summary>
    public bool MatchByName { get; set; }
    /// <summary>Who sees files with no ACL at all (Vault's default). Empty: every active Vault user. Or a Windows group or SID.</summary>
    public string? Everyone { get; set; }
    /// <summary>When lifecycle-state security applies but cannot be read: true hides the file (default), false uses folder security alone.</summary>
    public bool StrictStateSecurity { get; set; } = true;
}

/// <summary>Vault's users and groups, as read once per scan.</summary>
public sealed class VaultDirectory
{
    public Dictionary<long, VUser> Users { get; } = new();
    public Dictionary<long, VGroup> Groups { get; } = new();
    public Dictionary<long, (List<long> users, List<long> groups)> Members { get; } = new();

    public VaultDirectory(IEnumerable<VUser> users, IEnumerable<VGroup> groups)
    {
        foreach (var u in users) Users[u.Id] = u;
        foreach (var g in groups) Groups[g.Id] = g;
    }
}

/// <summary>The outcome for one file: Windows SIDs allowed and denied, or why nobody may see it.</summary>
public sealed record AccessResult(List<string> Allow, List<string> Deny, string? Problem);

/// <summary>
/// Vault ACLs → Silo allow/deny lists of Windows SIDs. Fails closed: whatever cannot be mapped grants nothing,
/// and a file whose security cannot be fully read gets an empty allow list (invisible) and a reason.
/// Vault rules followed (Vault Admin help, "Access Control Lists"): Deny beats Allow; an Allow through any group
/// is enough; with lifecycle "combined" security a user needs Read from both the object ACL and the state ACL.
/// </summary>
public sealed class AclMapper
{
    readonly VaultDirectory _dir;
    readonly AclOptions _o;
    readonly ISidResolver _sids;
    readonly Dictionary<long, string?> _userSid = new();
    readonly Dictionary<long, HashSet<long>> _expanded = new();
    List<string>? _everyone;
    public HashSet<string> Unmapped { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AclMapper(VaultDirectory dir, AclOptions o, ISidResolver sids) { _dir = dir; _o = o; _sids = sids; }

    static bool IsAd(string? auth) => auth != null && (auth.Contains("Active", StringComparison.OrdinalIgnoreCase) || auth.Contains("Win", StringComparison.OrdinalIgnoreCase) || auth.Equals("AD", StringComparison.OrdinalIgnoreCase));

    string Qualify(string name) => name.Contains('\\') || name.Contains('@') || string.IsNullOrEmpty(_o.Domain) ? name : _o.Domain + "\\" + name;

    public string? UserSid(long userId)
    {
        if (_userSid.TryGetValue(userId, out var cached)) return cached;
        string? sid = null;
        if (_dir.Users.TryGetValue(userId, out var u) && u.Active)
        {
            if (_o.UserMap.TryGetValue(u.Name, out var mapped)) sid = _sids.Sid(mapped);
            else if (IsAd(u.Auth) || _o.MatchByName) sid = _sids.Sid(Qualify(u.Name));
            if (sid == null) Unmapped.Add("user " + u.Name);
        }
        return _userSid[userId] = sid;
    }

    /// <summary>A group's own Windows SID, when it is an AD group or mapped to one.</summary>
    string? GroupSid(VGroup g)
    {
        if (_o.GroupMap.TryGetValue(g.Name, out var mapped)) return _sids.Sid(mapped);
        return IsAd(g.Auth) ? _sids.Sid(Qualify(g.Name)) : null;
    }

    /// <summary>Every active user in a user or group, through nested groups (cycles are fine).</summary>
    public HashSet<long> Users(long principalId)
    {
        if (_expanded.TryGetValue(principalId, out var done)) return done;
        var result = new HashSet<long>();
        var seen = new HashSet<long>();
        var stack = new Stack<long>([principalId]);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            if (_dir.Users.TryGetValue(id, out var u)) { if (u.Active) result.Add(id); continue; }
            if (!_dir.Groups.TryGetValue(id, out var g) || !g.Active) continue;
            if (_dir.Members.TryGetValue(id, out var m)) { foreach (var x in m.users) stack.Push(x); foreach (var x in m.groups) stack.Push(x); }
        }
        return _expanded[principalId] = result;
    }

    /// <summary>The SIDs that stand for a user or group: the group's own SID when Windows knows it, else its members'.</summary>
    public List<string> Sids(long principalId)
    {
        if (_dir.Users.ContainsKey(principalId)) return UserSid(principalId) is { } s ? [s] : [];
        if (_dir.Groups.TryGetValue(principalId, out var g))
        {
            if (!g.Active) return [];
            if (GroupSid(g) is { } gs) return [gs];
            return Users(principalId).Select(UserSid).OfType<string>().Distinct().ToList();
        }
        Unmapped.Add("principal id " + principalId);
        return [];
    }

    IEnumerable<long> AllActiveUsers() => _dir.Users.Values.Where(u => u.Active && !u.IsSystem).Select(u => u.Id);

    /// <summary>Who sees a file with no ACL: the configured group, or every active Vault user.</summary>
    public List<string> Everyone()
    {
        if (_everyone != null) return _everyone;
        if (!string.IsNullOrWhiteSpace(_o.Everyone)) return _everyone = _sids.Sid(_o.Everyone) is { } s ? [s] : [];
        return _everyone = AllActiveUsers().Select(UserSid).OfType<string>().Distinct().ToList();
    }

    bool? Read(VAce ace) => ace.Permissions.TryGetValue(_o.ReadPermissionId, out var v) ? v : null;

    /// <summary>
    /// Maps a file's security. <paramref name="obj"/> is the file's own ACL or the nearest folder ACL (null: none);
    /// <paramref name="state"/> is the lifecycle-state ACL when the behaviour says one applies.
    /// </summary>
    public AccessResult Map(VAcl? obj, string? behaviour, VAcl? state, bool stateExpected)
    {
        bool combined = behaviour != null && behaviour.Contains("Combin", StringComparison.OrdinalIgnoreCase);
        bool overrides = behaviour != null && behaviour.Contains("Overr", StringComparison.OrdinalIgnoreCase);
        if ((combined || overrides || stateExpected) && state == null)
        {
            if (_o.StrictStateSecurity) return new([], [], "lifecycle-state security applies but could not be read");
            combined = overrides = false;
        }
        if (overrides) { obj = state; combined = false; }

        if (!combined)
        {
            if (obj == null)
            {
                var all = Everyone();
                return new(all, [], all.Count == 0 ? "no Vault user maps to a Windows account" : null);
            }
            var allow = obj.Entries.Where(a => Read(a) == true).SelectMany(a => Sids(a.PrincipalId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var deny = obj.Entries.Where(a => Read(a) == false).SelectMany(a => Sids(a.PrincipalId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new(allow, deny, allow.Count == 0 ? "nobody in the Vault ACL maps to a Windows account" : null);
        }

        // Combined: a user needs Read from both gates, so work it out user by user.
        HashSet<long> Allowed(VAcl? acl) => acl == null ? AllActiveUsers().ToHashSet()
            : acl.Entries.Where(a => Read(a) == true).SelectMany(a => Users(a.PrincipalId)).ToHashSet();
        HashSet<long> Denied(VAcl? acl) => acl == null ? [] : acl.Entries.Where(a => Read(a) == false).SelectMany(a => Users(a.PrincipalId)).ToHashSet();
        var users = Allowed(obj);
        users.IntersectWith(Allowed(state));
        users.ExceptWith(Denied(obj));
        users.ExceptWith(Denied(state!));
        var allowSids = users.Select(UserSid).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var denySids = (obj?.Entries ?? []).Concat(state!.Entries).Where(a => Read(a) == false).SelectMany(a => Sids(a.PrincipalId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new(allowSids, denySids, allowSids.Count == 0 ? "no user passes both the folder and the lifecycle-state security" : null);
    }
}
