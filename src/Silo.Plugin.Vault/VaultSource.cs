using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Silo.Plugins.Vault;

/// <summary>
/// silo.source.vault: files in an Autodesk Vault (Basic, Workgroup or Professional), with their Vault path,
/// properties, revision and lifecycle state, and Vault's permissions mapped to Windows SIDs. The files are not
/// copied anywhere: the core asks for one ("fetch") when it reads it, and the copy goes to the scratch folder.
/// </summary>
public sealed class VaultSource : SourcePlugin
{
    public override PluginManifest Manifest { get; } = new()
    {
        Id = "silo.source.vault",
        Name = "Autodesk Vault",
        Version = "0.1.0",
        Kind = "source",
        Description = "Files in Autodesk Vault (Basic, Workgroup, Professional) with Vault paths, properties, revisions, lifecycle states and Vault permissions. Read-only.",
        Publisher = "Silo",
        Concurrency = 2,
        TimeoutSec = 86_400,
        Fetches = true,
        Fields =
        [
            new FieldSpec { Name = "vault_part", Label = "Part number", Type = "keyword", Show = true },
            new FieldSpec { Name = "vault_rev", Label = "Revision", Type = "keyword", Show = true },
            new FieldSpec { Name = "vault_state", Label = "Vault state", Type = "keyword", Facet = true, Show = true },
            new FieldSpec { Name = "vault_category", Label = "Vault category", Type = "keyword", Facet = true },
            new FieldSpec { Name = "vault_folder", Label = "Vault folder", Type = "keyword", Facet = true },
            new FieldSpec { Name = "vault_by", Label = "Checked in by", Type = "keyword", Facet = true },
            new FieldSpec { Name = "vault_project", Label = "Project", Type = "keyword", Facet = true, Show = true },
            new FieldSpec { Name = "vault_prop", Label = "Vault properties", Type = "text" },
        ],
        ConfigTemplate = new JsonObject
        {
            ["server"] = "http://vaultserver",
            ["vault"] = "Vault",
            ["user"] = "silo-indexer",
            ["password"] = "",
            ["windowsAuth"] = false,
            ["root"] = "$",
            ["exclude"] = new JsonArray("$/_Archive*", "*.bak", "*.dwl", "*.dwl2", "*.lck"),
            ["version"] = "released",
            ["unreleased"] = "latest",
            ["everyone"] = "",
            ["domain"] = "",
            ["userMap"] = new JsonObject(),
            ["openUrl"] = "explorer",
        },
    };

    /// <summary>The source's settings, read from the admin page's JSON.</summary>
    public sealed class Cfg
    {
        public VaultConnection Connection { get; } = new();
        public string Root = "$";
        public List<Regex> Exclude = new();
        public bool ReleasedOnly = true;
        public bool SkipNeverReleased;
        public AclOptions Acl = new();
        public bool AclByMaster = true;
        public string OpenUrl = "explorer";
        public bool IncludeHidden;

        public static Cfg From(JsonObject j)
        {
            var c = new Cfg();
            string? S(string k) => j[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            bool Bo(string k, bool d) => j[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d;
            c.Connection.Server = S("server") ?? "";
            c.Connection.Vault = S("vault") ?? "Vault";
            c.Connection.User = S("user");
            c.Connection.Password = S("password");
            c.Connection.Windows = Bo("windowsAuth", false);
            c.Connection.ServiceVersion = S("serviceVersion") is { Length: > 0 } sv ? sv : null;
            if (j["timeoutSec"] is JsonValue t && t.TryGetValue<int>(out var ts)) c.Connection.TimeoutSec = ts;
            c.Root = S("root") is { Length: > 0 } r ? r.TrimEnd('/') : "$";
            if (j["exclude"] is JsonArray ex)
                foreach (var e in ex.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)))
                    c.Exclude.Add(new Regex("^" + Regex.Escape(e!).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            c.ReleasedOnly = !string.Equals(S("version"), "latest", StringComparison.OrdinalIgnoreCase);
            c.SkipNeverReleased = string.Equals(S("unreleased"), "skip", StringComparison.OrdinalIgnoreCase);
            c.Acl.Everyone = S("everyone") is { Length: > 0 } ev ? ev : null;
            c.Acl.Domain = S("domain") is { Length: > 0 } dm ? dm : Environment.UserDomainName;
            c.Acl.MatchByName = Bo("matchByName", false);
            c.Acl.StrictStateSecurity = !string.Equals(S("stateSecurity"), "ignore", StringComparison.OrdinalIgnoreCase);
            if (j["readPermissionId"] is JsonValue rp && rp.TryGetValue<long>(out var rpi)) c.Acl.ReadPermissionId = rpi;
            if (j["userMap"] is JsonObject um) foreach (var (k, v) in um) if (v is JsonValue vv && vv.TryGetValue<string>(out var s)) c.Acl.UserMap[k] = s;
            if (j["groupMap"] is JsonObject gm) foreach (var (k, v) in gm) if (v is JsonValue vv && vv.TryGetValue<string>(out var s)) c.Acl.GroupMap[k] = s;
            c.AclByMaster = !string.Equals(S("aclEntity"), "file", StringComparison.OrdinalIgnoreCase);
            c.OpenUrl = S("openUrl") is { Length: > 0 } ou ? ou : "explorer";
            c.IncludeHidden = Bo("includeHidden", false);
            if (string.IsNullOrWhiteSpace(c.Connection.Server)) throw new ArgumentException("the Vault source needs 'server' (e.g. http://vaultserver)");
            if (!c.Connection.Windows && string.IsNullOrWhiteSpace(c.Connection.User)) throw new ArgumentException("the Vault source needs 'user' (or windowsAuth: true)");
            return c;
        }

        public bool Excluded(string vaultPath, string name) => Exclude.Any(r => r.IsMatch(vaultPath) || r.IsMatch(name));
    }

    readonly ISidResolver _sids;
    readonly Func<VaultConnection, VaultClient> _connect;

    public VaultSource() : this(new WindowsSidResolver(), c => new VaultClient(c)) { }
    public VaultSource(ISidResolver sids, Func<VaultConnection, VaultClient> connect) { _sids = sids; _connect = connect; }

    public Action<string>? Log;

    public override EnumerateResult Enumerate(EnumerateArgs args, Action<SourceItem> emit, CancellationToken ct) =>
        EnumerateAsync(args, emit, ct).GetAwaiter().GetResult();

    /// <summary>One full pass over the vault (Vault has no change feed cheap enough to use instead).</summary>
    public async Task<EnumerateResult> EnumerateAsync(EnumerateArgs args, Action<SourceItem> emit, CancellationToken ct, bool keepSignedIn = false)
    {
        var cfg = Cfg.From(args.Config);
        var vc = _connect(cfg.Connection);
        try { return await EnumerateWith(vc, cfg, emit, ct); }
        finally { if (!keepSignedIn) vc.Dispose(); }
    }

    async Task<EnumerateResult> EnumerateWith(VaultClient vc, Cfg cfg, Action<SourceItem> emit, CancellationToken ct)
    {
        if (Log != null) vc.Trace = Log;
        await vc.EnsureSignedInAsync(ct);
        var scan = await ScanContext.LoadAsync(vc, cfg, _sids, ct);
        long count = 0, hidden = 0, noAccess = 0;
        foreach (var folder in scan.Folders)
        {
            ct.ThrowIfCancellationRequested();
            var files = await vc.GetLatestFilesAsync(folder.Id, cfg.IncludeHidden, ct);
            files = files.Where(f => (cfg.IncludeHidden || !f.Hidden) && !f.Cloaked && !cfg.Excluded(folder.FullName + "/" + f.Name, f.Name)).ToList();
            if (files.Count == 0) continue;
            var chosen = new List<VFile>();
            foreach (var f in files)
            {
                var pick = cfg.ReleasedOnly ? await PickReleasedAsync(vc, f, cfg, ct) : f;
                if (pick != null) chosen.Add(pick);
            }
            foreach (var item in await scan.ItemsAsync(folder, chosen, ct))
            {
                if (item.Allow.Count == 0) noAccess++;
                emit(item); count++;
            }
            hidden += files.Count - chosen.Count;
        }
        if (scan.Mapper.Unmapped.Count > 0)
            Log?.Invoke($"Vault accounts with no Windows account (they get no access in Silo): {string.Join(", ", scan.Mapper.Unmapped.Take(20))}{(scan.Mapper.Unmapped.Count > 20 ? "…" : "")}. Use userMap to map Vault-only users.");
        if (noAccess > 0) Log?.Invoke($"{noAccess:N0} Vault files have permissions Silo could not map: they are indexed but nobody can see them.");
        Log?.Invoke($"Vault '{cfg.Connection.Vault}': {count:N0} files listed from {scan.Folders.Count:N0} folders{(hidden > 0 ? $", {hidden:N0} left out (never released, or excluded)" : "")}.");
        return new EnumerateResult { Count = count, Complete = true };
    }

    /// <summary>The newest released version, or the latest when the file was never released (unless told to skip those).</summary>
    static async Task<VFile?> PickReleasedAsync(VaultClient vc, VFile latest, Cfg cfg, CancellationToken ct)
    {
        if (latest.Released) return latest;
        if (latest.State == null) return latest;   // no lifecycle at all (Vault Basic, or none assigned): nothing to wait for
        var versions = await vc.GetVersionsAsync(latest.MasterId, ct);
        var rel = versions.Where(v => v.Released).MaxBy(v => v.VerNum);
        if (rel != null) return rel with { FolderId = latest.FolderId };
        return cfg.SkipNeverReleased ? null : latest;
    }

    public override SourceItem? Get(EnumerateArgs args, string key) => GetAsync(args, key, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<SourceItem?> GetAsync(EnumerateArgs args, string key, CancellationToken ct)
    {
        if (!TryMaster(key, out var master)) return null;
        var cfg = Cfg.From(args.Config);
        using var vc = _connect(cfg.Connection);
        vc.Trace = Log;
        await vc.SignInAsync(ct);
        var latest = await vc.GetLatestFileAsync(master, ct);
        if (latest == null || latest.Cloaked) return null;
        var pick = cfg.ReleasedOnly ? await PickReleasedAsync(vc, latest, cfg, ct) : latest;
        if (pick == null) return null;
        var scan = await ScanContext.LoadAsync(vc, cfg, _sids, ct);
        var folder = scan.Folders.FirstOrDefault(f => f.Id == latest.FolderId);
        if (folder == null) return null;
        return (await scan.ItemsAsync(folder, [pick], ct)).FirstOrDefault();
    }

    static bool TryMaster(string key, out long master) { master = 0; return key.StartsWith("m:", StringComparison.Ordinal) && long.TryParse(key.AsSpan(2), out master); }

    /// <summary>Downloads the indexed version of an item into <c>dir</c> (the scratch folder). Returns the path.</summary>
    public override string? Fetch(EnumerateArgs args, string key) => Fetch(args, key, null);
    public override string? Fetch(EnumerateArgs args, string key, string? dir) => FetchAsync(args, key, dir, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<string?> FetchAsync(EnumerateArgs args, string key, string? dir, CancellationToken ct)
    {
        if (!TryMaster(key, out var master)) return null;
        var cfg = Cfg.From(args.Config);
        // The file id of the version that was indexed rides in the key's version; ask Vault again to be safe.
        using var vc = _connect(cfg.Connection);
        vc.Trace = Log;
        await vc.SignInAsync(ct);
        var latest = await vc.GetLatestFileAsync(master, ct);
        if (latest == null) return null;
        var pick = cfg.ReleasedOnly ? await PickReleasedAsync(vc, latest, cfg, ct) : latest;
        if (pick == null) return null;
        dir ??= args.Config["fetchDir"]?.GetValue<string>() ?? Path.GetTempPath();
        var sub = Path.Combine(dir, "silo-fetch-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(sub);
        // Keep the real name so readers that look at extensions and sibling names behave as on a share.
        var path = Path.Combine(sub, SafeName(pick.Name));
        try { await vc.DownloadAsync(pick, path, ct); }
        catch { try { Directory.Delete(sub, true); } catch { } throw; }
        return path;
    }

    static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray()).Trim('.', ' ');
        return s.Length == 0 ? "file" : s;
    }

    /// <summary>What one scan needs to turn Vault files into items: folders, properties, users, groups and ACLs.</summary>
    sealed class ScanContext
    {
        public required List<VFolder> Folders;
        public required AclMapper Mapper;
        public required Dictionary<long, VPropDef> PropDefs;
        public required Dictionary<long, VEntAcl> FolderAcl;
        public required Dictionary<long, VAcl> Acls;
        public required Dictionary<long, VFolder> ById;
        public required VaultClient Vc;
        public required Cfg Cfg;

        public static async Task<ScanContext> LoadAsync(VaultClient vc, Cfg cfg, ISidResolver sids, CancellationToken ct)
        {
            var root = cfg.Root is "$" or "$/" ? await vc.GetFolderRootAsync(ct) : await vc.GetFolderByPathAsync(cfg.Root, ct);
            var folders = new List<VFolder> { root };
            folders.AddRange(await vc.GetFoldersAsync(root.Id, true, ct));
            // Cloaked folders are ones the account cannot see; excluded folders take their subfolders with them.
            var ids = new HashSet<long>();
            var keep = new List<VFolder>();
            var byId = folders.ToDictionary(f => f.Id);
            foreach (var f in folders.OrderBy(f => f.FullName.Length))
            {
                if (f.Cloaked || cfg.Excluded(f.FullName, f.Name)) continue;
                if (f.Id != root.Id && !ids.Contains(f.ParentId)) continue;
                ids.Add(f.Id); keep.Add(f);
            }
            var users = await vc.GetUsersAsync(ct);
            var groups = await vc.GetGroupsAsync(ct);
            var dir = new VaultDirectory(users, groups);
            foreach (var g in groups.Where(g => g.Active)) dir.Members[g.Id] = await vc.GetGroupMembersAsync(g.Id, ct);
            var defs = (await vc.GetFilePropertyDefinitionsAsync(ct)).ToDictionary(d => d.Id);
            var (ents, acls) = await vc.GetAclsAsync(keep.Select(f => f.Id).ToList(), ct);
            return new ScanContext
            {
                Folders = keep, Mapper = new AclMapper(dir, cfg.Acl, sids), PropDefs = defs,
                FolderAcl = ents.GroupBy(e => e.EntityId).ToDictionary(g => g.Key, g => g.First()), Acls = acls, ById = byId, Vc = vc, Cfg = cfg,
            };
        }

        /// <summary>The nearest folder (this one or a parent) with its own ACL.</summary>
        VEntAcl? FolderSecurity(long folderId)
        {
            for (int guard = 0; guard < 200 && folderId > 0; guard++)
            {
                if (FolderAcl.TryGetValue(folderId, out var e) && e.AclId > 0) return e;
                if (!ById.TryGetValue(folderId, out var f) || f.ParentId == folderId) break;
                folderId = f.ParentId;
            }
            return null;
        }

        public async Task<List<SourceItem>> ItemsAsync(VFolder folder, List<VFile> files, CancellationToken ct)
        {
            var props = (await Vc.GetFilePropertiesAsync(files.Select(f => f.Id).ToList(), ct)).ToLookup(p => p.EntityId);
            var (ents, acls) = await Vc.GetAclsAsync(files.Select(f => Cfg.AclByMaster ? f.MasterId : f.Id).Distinct().ToList(), ct);
            foreach (var (k, v) in acls) Acls[k] = v;
            var entBy = ents.GroupBy(e => e.EntityId).ToDictionary(g => g.Key, g => g.First());
            var items = new List<SourceItem>();
            foreach (var f in files)
            {
                entBy.TryGetValue(Cfg.AclByMaster ? f.MasterId : f.Id, out var own);
                var access = Access(f, own, folder);
                items.Add(Item(folder, f, props[f.Id], access));
            }
            return items;
        }

        AccessResult Access(VFile f, VEntAcl? own, VFolder folder)
        {
            // The file's own (override) ACL wins; otherwise the nearest folder ACL; otherwise none (everyone).
            var ent = own is { AclId: > 0 } ? own : FolderSecurity(folder.Id);
            VAcl? obj = null;
            if (ent != null && !Acls.TryGetValue(ent.AclId, out obj)) return new([], [], $"ACL {ent.AclId} was not returned by Vault");
            // Lifecycle-state security only applies when Vault names a state ACL for the file; the behaviour flag
            // alone ("Combined" is the default on every entity) means nothing without one.
            var sysIds = own?.SystemAclIds ?? [];
            bool expected = sysIds.Count > 0;
            var behaviour = expected ? own!.Behaviour ?? "Combined" : null;
            if (behaviour != null && behaviour.Equals("None", StringComparison.OrdinalIgnoreCase)) behaviour = "Combined";
            VAcl? state = sysIds.Select(id => Acls.GetValueOrDefault(id)).FirstOrDefault(a => a != null);
            return Mapper.Map(obj, behaviour, state, expected);
        }

        SourceItem Item(VFolder folder, VFile f, IEnumerable<VProp> props, AccessResult access)
        {
            var path = folder.FullName.TrimEnd('/') + "/" + f.Name;
            var meta = new Dictionary<string, List<string>>();
            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var text = new StringBuilder();
            foreach (var p in props)
            {
                if (string.IsNullOrWhiteSpace(p.Value) || !PropDefs.TryGetValue(p.PropDefId, out var d)) continue;
                named.TryAdd(d.DisplayName, p.Value!);
                if (d.SystemName != null) named.TryAdd(d.SystemName, p.Value!);
                text.Append(d.DisplayName).Append(": ").Append(p.Value).Append('\n');
            }
            string? P(params string[] names) => names.Select(n => named.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var title = P("Title", "Title (Item,CO)");
            var description = P("Description", "Description (Item,CO)");
            var part = P("Part Number", "PartNumber");
            var project = P("Project", "Project Name", "Job Number", "Job");
            meta.Add("title", title ?? Path.GetFileNameWithoutExtension(f.Name));
            meta.Add("abstract", description);
            meta.Add("author", P("Author", "Designer", "Created By") ?? f.CreatedBy);
            meta.Add("vault_part", part);
            meta.Add("vault_rev", f.Revision ?? P("Revision", "Revision Number"));
            meta.Add("vault_state", f.State ?? (f.Released ? "Released" : null));
            meta.Add("vault_category", f.Category ?? P("Category Name"));
            meta.Add("vault_folder", folder.FullName);
            meta.Add("vault_by", f.CreatedBy);
            meta.Add("vault_project", project);
            if (text.Length > 0) meta.Add("vault_prop", text.ToString(0, Math.Min(text.Length, 8000)));
            meta.Add("vault_version", f.VerNum.ToString());
            if (f.Comment is { Length: > 0 } cm) meta.Add("vault_comment", cm);
            if (f.CheckedIn is { } ck) meta.Add("year", ck.Year.ToString());
            if (access.Problem != null) meta.Add("access_problem", access.Problem);

            var item = new SourceItem
            {
                Key = "m:" + f.MasterId,
                Name = f.Name,
                Ext = Path.GetExtension(f.Name).ToLowerInvariant() is { Length: > 0 } e ? e : null,
                Size = f.Size,
                Modified = f.CheckedIn ?? f.Modified,
                Created = f.Created,
                Location = path,
                Url = OpenLink(path, f),
                Allow = access.Allow,
                Deny = access.Deny,
                Meta = meta,
            };
            item.Meta.Add("landing_label", "Open in Vault");
            // The version changes when the file, its properties or its permissions change; only a new file id
            // (a new check-in) or a size/date change makes the core read the content again.
            item.Version = f.Id + ":" + Hash(JsonSerializer.Serialize(new { access.Allow, access.Deny, meta }));
            return item;
        }

        string? OpenLink(string vaultPath, VFile f)
        {
            var server = Vc.Base.ToString().TrimEnd('/');
            var vault = Cfg.Connection.Vault;
            var t = Cfg.OpenUrl switch
            {
                "explorer" => "{server}/AutodeskDM/Services/EntityDataCommandRequest.aspx?Vault={vault}&ObjectId={pathEncoded}&ObjectType=File&Command=Select",
                "none" => null,
                var custom => custom,
            };
            if (t == null) return null;
            var url = t.Replace("{server}", server).Replace("{vault}", Uri.EscapeDataString(vault)).Replace("{pathEncoded}", Uri.EscapeDataString(vaultPath))
                .Replace("{path}", vaultPath).Replace("{masterId}", f.MasterId.ToString()).Replace("{fileId}", f.Id.ToString());
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;
        }

        static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
    }
}
