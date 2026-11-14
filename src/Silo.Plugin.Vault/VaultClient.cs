using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Xml.Linq;

namespace Silo.Plugins.Vault;

public sealed record VFolder(long Id, long ParentId, string FullName, string Name, bool Cloaked, bool IsLibrary);

public sealed record VFile
{
    public long Id { get; init; }
    public long MasterId { get; init; }
    public long FolderId { get; init; }
    public string Name { get; init; } = "";
    public int VerNum { get; init; }
    public long Size { get; init; }
    public DateTime? CheckedIn { get; init; }
    public DateTime? Created { get; init; }
    public DateTime? Modified { get; init; }
    public string? CreatedBy { get; init; }
    public string? Comment { get; init; }
    public string? Category { get; init; }
    public string? Revision { get; init; }
    public string? State { get; init; }
    public long? StateId { get; init; }
    /// <summary>The lifecycle state counts as released ("Consume" in Vault's terms).</summary>
    public bool Released { get; init; }
    public bool Hidden { get; init; }
    public bool Cloaked { get; init; }
    public bool CheckedOut { get; init; }
}

public sealed record VUser(long Id, string Name, string? Auth, bool Active, string? Email, string? FirstName, string? LastName, bool IsSystem);
public sealed record VGroup(long Id, string Name, string? Auth, bool Active, bool IsSystem);
public sealed record VAce(long PrincipalId, Dictionary<long, bool> Permissions);
public sealed record VAcl(long Id, List<VAce> Entries);
/// <summary>An entity's security: its own ACL id (0 when it has none), how lifecycle-state security combines, and any state ACL ids.</summary>
public sealed record VEntAcl(long EntityId, long AclId, string? Behaviour, List<long> SystemAclIds);
public sealed record VProp(long EntityId, long PropDefId, string? Value);
public sealed record VPropDef(long Id, string DisplayName, string? SystemName);

/// <summary>Settings for reaching one vault.</summary>
public sealed class VaultConnection
{
    /// <summary>"http://vaultserver" (port and https allowed).</summary>
    public string Server { get; set; } = "";
    public string Vault { get; set; } = "Vault";
    public string? User { get; set; }
    public string? Password { get; set; }
    /// <summary>Sign in with the Windows account the plugin runs as (Vault with Active Directory).</summary>
    public bool Windows { get; set; }
    /// <summary>Service version folder, e.g. "v31". Found automatically when empty.</summary>
    public string? ServiceVersion { get; set; }
    public int TimeoutSec { get; set; } = 300;
}

/// <summary>
/// The parts of Vault's web services Silo needs, called over plain SOAP. Every call is a read: the plugin signs in
/// read-only where the server offers it and never checks out, changes or deletes anything.
/// </summary>
public sealed class VaultClient : IDisposable
{
    readonly VaultConnection _c;
    readonly HttpClient _http;
    readonly Dictionary<string, SoapService> _svc = new();
    XElement? _security;
    public string? ServiceVersion { get; private set; }
    public bool ReadOnlySignIn { get; private set; }
    public long UserId { get; private set; }
    public Action<string>? Trace;

    public VaultClient(VaultConnection c, HttpMessageHandler? handler = null)
    {
        _c = c;
        if (!Uri.TryCreate(c.Server.Contains("://") ? c.Server : "http://" + c.Server, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            throw new ArgumentException($"'{c.Server}' is not a Vault server address (expected http://server or https://server)");
        Base = new Uri(u.GetLeftPart(UriPartial.Authority) + "/");
        handler ??= new HttpClientHandler { UseDefaultCredentials = c.Windows, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(30, c.TimeoutSec)) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SiloServer-Vault/1.0");
    }

    public Uri Base { get; }

    /// <summary>Host name as Vault expects it in "dataServer".</summary>
    string DataServer => Base.IsDefaultPort ? Base.Host : $"{Base.Host}:{Base.Port}";

    // ---------- service discovery ----------

    static readonly string[] Suffixes = ["", "_3", "_2", "_1"];

    /// <summary>Finds the newest service version the server answers to (v20..v40), unless one is set.</summary>
    public async Task<string> FindServiceVersionAsync(CancellationToken ct)
    {
        if (ServiceVersion != null) return ServiceVersion;
        var candidates = !string.IsNullOrWhiteSpace(_c.ServiceVersion) ? [_c.ServiceVersion!.Trim('/')]
            : Enumerable.Range(20, 21).Reverse().SelectMany(n => Suffixes.Select(s => $"v{n}{s}")).ToArray();
        foreach (var v in candidates)
        {
            try
            {
                using var r = await _http.GetAsync(new Uri(Base, $"AutodeskDM/Services/Filestore/{v}/AuthService.svc?wsdl"), ct);
                if (r.IsSuccessStatusCode) { ServiceVersion = v; Trace?.Invoke($"Vault web services found at /AutodeskDM/Services/{v}/"); return v; }
            }
            catch (HttpRequestException e) when (e.StatusCode == null)
            {
                throw new InvalidOperationException($"cannot reach {Base}: {e.Message}");
            }
        }
        throw new InvalidOperationException($"no Autodesk Vault web services found at {Base}AutodeskDM/Services (tried {candidates[0]}..{candidates[^1]}). Is this the Vault server (ADMS) address?");
    }

    static readonly Dictionary<string, string[]> Paths = new()
    {
        ["AuthService"] = ["Filestore/{v}/AuthService.svc"],
        ["WinAuthService"] = ["Filestore/{v}/WinAuth/WinAuthService.svc", "Filestore/{v}/WinAuthService.svc"],
        ["FilestoreService"] = ["Filestore/{v}/FilestoreService.svc"],
        ["FilestoreVaultService"] = ["Filestore/{v}/FilestoreVaultService.svc"],
        ["InformationService"] = ["Filestore/{v}/InformationService.svc", "{v}/InformationService.svc"],
        ["DocumentService"] = ["{v}/DocumentService.svc"],
        ["PropertyService"] = ["{v}/PropertyService.svc"],
        ["SecurityService"] = ["{v}/SecurityService.svc"],
        ["AdminService"] = ["{v}/AdminService.svc"],
    };

    public async Task<SoapService> ServiceAsync(string name, CancellationToken ct)
    {
        if (_svc.TryGetValue(name, out var s)) return s;
        var v = await FindServiceVersionAsync(ct);
        Exception? last = null;
        foreach (var p in Paths[name])
        {
            try
            {
                s = await SoapService.LoadAsync(_http, name, new Uri(Base, "AutodeskDM/Services/" + p.Replace("{v}", v)), ct);
                return _svc[name] = s;
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException) { last = e; }
        }
        throw new InvalidOperationException($"{name} not found on this Vault: {last?.Message}");
    }

    // ---------- sign in ----------

    public async Task SignInAsync(CancellationToken ct)
    {
        SoapService auth;
        var args = new Dictionary<string, object?> { ["dataServer"] = DataServer, ["knowledgeVault"] = _c.Vault };
        if (_c.Windows) auth = await ServiceAsync("WinAuthService", ct);
        else
        {
            auth = await ServiceAsync("AuthService", ct);
            args["userName"] = _c.User ?? "";
            args["userPassword"] = _c.Password ?? "";
        }
        // Read-only sign-ins are meant for integrations like this one and do not take a client licence.
        string op = auth.Has("SignInReadOnly") ? "SignInReadOnly" : "SignIn";
        ReadOnlySignIn = op == "SignInReadOnly";
        var known = auth.ParamsOf(op).ToHashSet();
        foreach (var k in args.Keys.Where(k => !known.Contains(k)).ToList()) args.Remove(k);
        var reply = await auth.CallAsync(_http, op, args, null, ct);
        var sec = reply.Header?.Elements().FirstOrDefault(e => e.Name.LocalName == "SecurityHeader")
            ?? throw new InvalidOperationException("Vault accepted the sign-in but sent no security ticket");
        _security = new XElement(sec);
        UserId = long.TryParse(Field(sec, "UserId"), out var uid) ? uid : 0;
        Trace?.Invoke($"Signed in to vault '{_c.Vault}' as {(_c.Windows ? "the Windows account" : _c.User)} ({(ReadOnlySignIn ? "read-only" : "full")} sign-in, user id {UserId})");
    }

    public async Task EnsureSignedInAsync(CancellationToken ct) { if (_security == null) await SignInAsync(ct); }

    public async Task SignOutAsync()
    {
        if (_security == null) return;
        try
        {
            var auth = await ServiceAsync(_c.Windows ? "WinAuthService" : "AuthService", CancellationToken.None);
            if (auth.Has("SignOut")) await auth.CallAsync(_http, "SignOut", new Dictionary<string, object?>(), _security, CancellationToken.None);
        }
        catch { }
        _security = null;
    }

    /// <summary>Calls an operation with the sign-in ticket; signs in again once if the ticket has run out.</summary>
    public async Task<SoapReply> CallAsync(string service, string op, Dictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        if (_security == null) await SignInAsync(ct);
        var s = await ServiceAsync(service, ct);
        try { return await s.CallAsync(_http, op, args ?? new(), _security, ct); }
        catch (VaultFault f) when (f.Code is VaultErrors.BadTicket or VaultErrors.SessionExpired)
        {
            Trace?.Invoke("Vault session ran out: signing in again");
            await SignInAsync(ct);
            return await s.CallAsync(_http, op, args ?? new(), _security, ct);
        }
    }

    // ---------- reading ----------

    public static string? Field(XElement e, string name) =>
        e.Attribute(name)?.Value ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value;

    static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name);
    static long L(XElement e, string name) => long.TryParse(Field(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    static bool B(XElement e, string name) => string.Equals(Field(e, name), "true", StringComparison.OrdinalIgnoreCase);
    static DateTime? D(XElement e, string name)
    {
        var s = Field(e, name);
        if (s == null || !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)) return null;
        return d.Year < 1900 ? null : d;
    }

    /// <summary>A single record: SOAP puts its fields on the result element itself, but accept a wrapped one too.</summary>
    static XElement? One(XElement? result, string name)
    {
        if (result == null) return null;
        var inner = result.Elements().Where(e => e.Name.LocalName == name).ToList();
        if (inner.Count == 1 && !result.HasAttributes) return inner[0];
        return result.HasAttributes || result.HasElements ? result : null;
    }

    /// <summary>Child records of a result by local name (e.g. every "File" under GetLatestFilesByFolderIdResult).</summary>
    static IEnumerable<XElement> Records(XElement? result, string name)
    {
        if (result == null) return [];
        if (result.Name.LocalName == name) return [result];
        return result.Elements().Where(e => e.Name.LocalName == name);
    }

    public static VFolder ParseFolder(XElement e) => new(L(e, "Id"), L(e, "ParId"), Field(e, "FullName") ?? "", Field(e, "Name") ?? "", B(e, "Cloaked"), B(e, "IsLib"));

    public static VFile ParseFile(XElement e)
    {
        var rev = Child(e, "FileRev");
        var lc = Child(e, "FileLfCyc");
        var cat = Child(e, "Cat");
        return new VFile
        {
            Id = L(e, "Id"), MasterId = L(e, "MasterId"), FolderId = L(e, "FolderId"), Name = Field(e, "Name") ?? "",
            VerNum = (int)L(e, "VerNum"), Size = L(e, "FileSize"),
            CheckedIn = D(e, "CkInDate"), Created = D(e, "CreateDate"), Modified = D(e, "ModDate"),
            CreatedBy = Field(e, "CreateUserName"), Comment = Field(e, "Comm"),
            Category = cat == null ? null : Field(cat, "CatName"),
            Revision = rev == null ? null : Field(rev, "Label"),
            State = lc == null ? null : Field(lc, "LfCycStateName"),
            StateId = lc == null ? null : L(lc, "LfCycStateId") is var sid and > 0 ? sid : null,
            Released = lc != null && B(lc, "Consume"),
            Hidden = B(e, "Hidden"), Cloaked = B(e, "Cloaked"), CheckedOut = B(e, "CheckedOut"),
        };
    }

    public async Task<VFolder> GetFolderRootAsync(CancellationToken ct)
    {
        var r = await CallAsync("DocumentService", "GetFolderRoot", null, ct);
        return ParseFolder(One(r.Result, "Folder") ?? throw new InvalidOperationException("Vault returned no root folder"));
    }

    public async Task<VFolder> GetFolderByPathAsync(string path, CancellationToken ct)
    {
        var r = await CallAsync("DocumentService", "GetFolderByPath", new() { ["path"] = path }, ct);
        return ParseFolder(One(r.Result, "Folder") ?? throw new InvalidOperationException($"no folder {path} in Vault"));
    }

    public async Task<List<VFolder>> GetFoldersAsync(long parentId, bool recurse, CancellationToken ct)
    {
        var r = await CallAsync("DocumentService", "GetFoldersByParentId", new() { ["parentFolderId"] = parentId, ["recurse"] = recurse }, ct);
        return Records(r.Result, "Folder").Select(ParseFolder).ToList();
    }

    public async Task<List<VFile>> GetLatestFilesAsync(long folderId, bool includeHidden, CancellationToken ct)
    {
        var r = await CallAsync("DocumentService", "GetLatestFilesByFolderId", new() { ["folderId"] = folderId, ["includeHidden"] = includeHidden }, ct);
        return Records(r.Result, "File").Select(ParseFile).ToList();
    }

    /// <summary>Every version of one file, oldest first.</summary>
    public async Task<List<VFile>> GetVersionsAsync(long masterId, CancellationToken ct)
    {
        var r = await CallAsync("DocumentService", "GetFilesByMasterId", new() { ["fileMasterId"] = masterId }, ct);
        return Records(r.Result, "File").Select(ParseFile).OrderBy(f => f.VerNum).ToList();
    }

    public async Task<VFile?> GetLatestFileAsync(long masterId, CancellationToken ct)
    {
        var r = await CallAsync("DocumentService", "GetLatestFileByMasterId", new() { ["fileMasterId"] = masterId }, ct);
        return One(r.Result, "File") is { } f ? ParseFile(f) : null;
    }

    public async Task<VFile> GetFileAsync(long fileId, CancellationToken ct)
    {
        var r = await CallAsync("DocumentService", "GetFileById", new() { ["fileId"] = fileId }, ct);
        return ParseFile(One(r.Result, "File") ?? throw new InvalidOperationException($"no file {fileId} in Vault"));
    }

    public async Task<List<VPropDef>> GetFilePropertyDefinitionsAsync(CancellationToken ct)
    {
        var r = await CallAsync("PropertyService", "GetPropertyDefinitionsByEntityClassId", new() { ["entityClassId"] = "FILE" }, ct);
        return Records(r.Result, "PropDef").Select(e => new VPropDef(L(e, "Id"), Field(e, "DispName") ?? "", Field(e, "SysName"))).ToList();
    }

    public async Task<List<VProp>> GetFilePropertiesAsync(IReadOnlyList<long> fileIds, CancellationToken ct)
    {
        var all = new List<VProp>();
        foreach (var chunk in fileIds.Chunk(200))
        {
            var r = await CallAsync("PropertyService", "GetPropertiesByEntityIds", new() { ["entityClassId"] = "FILE", ["entityIds"] = chunk }, ct);
            all.AddRange(Records(r.Result, "PropInst").Select(e => new VProp(L(e, "EntityId"), L(e, "PropDefId"), Field(e, "Val"))));
        }
        return all;
    }

    /// <summary>Security for folders or files: each entity's ACL reference and the ACLs themselves.</summary>
    public async Task<(List<VEntAcl> ents, Dictionary<long, VAcl> acls)> GetAclsAsync(IReadOnlyList<long> entityIds, CancellationToken ct)
    {
        var ents = new List<VEntAcl>();
        var acls = new Dictionary<long, VAcl>();
        foreach (var chunk in entityIds.Chunk(200))
        {
            var r = await CallAsync("SecurityService", "GetEntACLsByEntityIds", new() { ["entityIds"] = chunk }, ct);
            var (e, a) = ParseAcls(r.Result);
            ents.AddRange(e);
            foreach (var (k, v) in a) acls[k] = v;
        }
        return (ents, acls);
    }

    /// <summary>Parses EntsAndACLs. Public for tests and the probe.</summary>
    public static (List<VEntAcl> ents, Dictionary<long, VAcl> acls) ParseAcls(XElement? result)
    {
        var ents = new List<VEntAcl>();
        var acls = new Dictionary<long, VAcl>();
        if (result == null) return (ents, acls);
        foreach (var e in result.Descendants().Where(x => x.Name.LocalName == "EntACL"))
        {
            // State (system) ACLs: Vault names these SysAclId / SysACLIds depending on the version.
            var sys = e.Attributes().Where(a => a.Name.LocalName.StartsWith("SysA", StringComparison.OrdinalIgnoreCase) && a.Name.LocalName.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Value)
                .Concat(e.Elements().Where(x => x.Name.LocalName.StartsWith("SysA", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(x => x.HasElements ? x.Descendants().Where(d => !d.HasElements).Select(d => d.Value) : [x.Value]))
                .Select(v => long.TryParse(v, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();
            ents.Add(new VEntAcl(L(e, "EntId"), L(e, "ACLId"), Field(e, "SysAclBeh"), sys));
        }
        foreach (var a in result.Descendants().Where(x => x.Name.LocalName == "ACL"))
        {
            var entries = new List<VAce>();
            foreach (var ace in a.Descendants().Where(x => x.Name.LocalName == "ACE"))
            {
                var perms = new Dictionary<long, bool>();
                foreach (var p in ace.Descendants().Where(x => x.Name.LocalName == "AccessPermis"))
                {
                    var id = L(p, "Id");
                    var val = Field(p, "Val");
                    if (id > 0 && val != null) perms[id] = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                }
                entries.Add(new VAce(L(ace, "UserGrpId"), perms));
            }
            acls[L(a, "Id")] = new VAcl(L(a, "Id"), entries);
        }
        return (ents, acls);
    }

    public async Task<List<VUser>> GetUsersAsync(CancellationToken ct)
    {
        var r = await CallAsync("AdminService", "GetAllUsers", null, ct);
        return Records(r.Result, "User").Select(ParseUser).ToList();
    }

    static VUser ParseUser(XElement e) => new(L(e, "Id"), Field(e, "Name") ?? "", Field(e, "Auth"), Field(e, "IsActive") is not { } a || a.Equals("true", StringComparison.OrdinalIgnoreCase),
        Field(e, "Email"), Field(e, "FirstName"), Field(e, "LastName"), B(e, "IsSys"));

    static VGroup ParseGroup(XElement e) => new(L(e, "Id"), Field(e, "Name") ?? "", Field(e, "Auth"), Field(e, "IsActive") is not { } a || a.Equals("true", StringComparison.OrdinalIgnoreCase), B(e, "IsSys"));

    public async Task<List<VGroup>> GetGroupsAsync(CancellationToken ct)
    {
        var r = await CallAsync("AdminService", "GetAllGroups", null, ct);
        return Records(r.Result, "Group").Select(ParseGroup).ToList();
    }

    /// <summary>Direct members of a group: users and groups.</summary>
    public async Task<(List<long> users, List<long> groups)> GetGroupMembersAsync(long groupId, CancellationToken ct)
    {
        var admin = await ServiceAsync("AdminService", ct);
        if (admin.Has("GetGroupInfoByGroupId"))
        {
            var r = await CallAsync("AdminService", "GetGroupInfoByGroupId", new() { ["groupId"] = groupId }, ct);
            var info = r.Result;
            var users = Child(info!, "Users")?.Elements().Where(e => e.Name.LocalName == "User").Select(e => L(e, "Id")).ToList() ?? [];
            var groups = Child(info!, "Groups")?.Elements().Where(e => e.Name.LocalName == "Group").Select(e => L(e, "Id")).ToList() ?? [];
            return (users, groups);
        }
        var u = await CallAsync("AdminService", "GetMemberUsersByGroupId", new() { ["groupId"] = groupId }, ct);
        var g = await CallAsync("AdminService", "GetMemberGroupsByGroupId", new() { ["groupId"] = groupId }, ct);
        return (Records(u.Result, "User").Select(e => L(e, "Id")).ToList(), Records(g.Result, "Group").Select(e => L(e, "Id")).ToList());
    }

    // ---------- download ----------

    /// <summary>
    /// Downloads one file version to <paramref name="path"/>, in parts. Vault hands out a download ticket for the
    /// version, then the filestore streams the bytes (MTOM or inline).
    /// </summary>
    public async Task DownloadAsync(VFile file, string path, CancellationToken ct, long partBytes = 32L << 20)
    {
        var t = await CallAsync("DocumentService", "GetDownloadTicketsByFileIds", new() { ["fileIds"] = new[] { file.Id } }, ct);
        var ticket = Records(t.Result, "ByteArray").FirstOrDefault() ?? t.Result?.Elements().FirstOrDefault()
            ?? throw new InvalidOperationException("Vault gave no download ticket");
        var fs = await ServiceAsync("FilestoreService", ct);
        var ticketParam = fs.ParamsOf("DownloadFilePart").FirstOrDefault(p => p.Contains("ticket", StringComparison.OrdinalIgnoreCase)) ?? "ticket";
        var ticketBytes = Field(ticket, "Bytes");
        await using var outFile = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        long first = 0;
        long size = file.Size;
        for (int guard = 0; guard < 100_000; guard++)
        {
            long last = size > 0 ? Math.Min(size, first + partBytes) - 1 : first + partBytes - 1;
            var args = new Dictionary<string, object?>
            {
                // A ticket is a ByteArray record on most versions; pass it back as it came.
                [ticketParam] = ticketBytes != null ? new SoapRecord([("Bytes", Convert.FromBase64String(ticketBytes))]) : ticket,
                ["firstByte"] = first, ["lastByte"] = last, ["allowSync"] = true,
            };
            foreach (var k in args.Keys.Where(k => !fs.ParamsOf("DownloadFilePart").Contains(k)).ToList()) args.Remove(k);
            var r = await CallAsync("FilestoreService", "DownloadFilePart", args, ct);
            var data = PartBytes(r);
            var hdr = r.Header?.Elements().FirstOrDefault(e => e.Name.LocalName == "FileTransferHeader");
            var compression = hdr == null ? null : Field(hdr, "Compression");
            if (compression is { } c && !c.Equals("None", StringComparison.OrdinalIgnoreCase)) data = Decompress(data, c);
            await outFile.WriteAsync(data, ct);
            first += data.Length;
            bool complete = hdr != null && Field(hdr, "IsComplete") is { } ic && ic.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (data.Length == 0 || complete || (size > 0 && first >= size)) break;
        }
        if (size > 0 && first != size) throw new InvalidOperationException($"download of {file.Name} stopped at {first:N0} of {size:N0} bytes");
    }

    static byte[] PartBytes(SoapReply r)
    {
        var res = r.Result;
        if (res != null && !res.HasElements && res.Value.Length > 0) return Convert.FromBase64String(res.Value.Trim());
        if (res != null) foreach (var e in res.DescendantsAndSelf().Where(e => !e.HasElements && e.Value.Length > 0)) return Convert.FromBase64String(e.Value.Trim());
        return r.Parts.Values.FirstOrDefault() ?? [];
    }

    static byte[] Decompress(byte[] data, string kind)
    {
        using var input = new MemoryStream(data);
        using Stream z = kind.Contains("gzip", StringComparison.OrdinalIgnoreCase) ? new GZipStream(input, CompressionMode.Decompress)
            : kind.Contains("zlib", StringComparison.OrdinalIgnoreCase) ? new ZLibStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    public void Dispose() { try { SignOutAsync().Wait(5000); } catch { } _http.Dispose(); }
}
