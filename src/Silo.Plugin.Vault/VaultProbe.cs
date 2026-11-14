using System.Text;
using System.Text.Json.Nodes;

namespace Silo.Plugins.Vault;

/// <summary>
/// Silo.PluginHost.exe --vault-probe &lt;server&gt; &lt;vault&gt; &lt;user&gt; [--windows] [--out file]
/// Signs in read-only, looks at a sample of folders, files, properties and permissions, downloads one small file,
/// and writes what it saw to vault-probe.txt. It changes nothing in Vault and never writes the password anywhere.
/// The report is what tunes the plugin to a site's Vault version.
/// </summary>
public static class VaultProbe
{
    public static int Run(string[] args)
    {
        var pos = args.Where(a => !a.StartsWith("--")).ToList();
        bool windows = args.Contains("--windows");
        var outIdx = Array.IndexOf(args, "--out");
        var outPath = outIdx >= 0 && outIdx + 1 < args.Length ? args[outIdx + 1] : Path.Combine(Environment.CurrentDirectory, "vault-probe.txt");
        if (outIdx >= 0 && outIdx + 1 < args.Length) pos.Remove(args[outIdx + 1]);
        if (pos.Count < 2 || (!windows && pos.Count < 3))
        {
            Console.Error.WriteLine("Usage: Silo.PluginHost.exe --vault-probe <server> <vault> <user> [--windows] [--out vault-probe.txt]");
            Console.Error.WriteLine("  e.g. Silo.PluginHost.exe --vault-probe http://vaultserver Vault silo-indexer");
            return 2;
        }
        string? password = null;
        if (!windows)
        {
            password = Environment.GetEnvironmentVariable("SILO_VAULT_PASSWORD");
            if (password == null) { Console.Write($"Vault password for {pos[2]}: "); password = ReadSecret(); }
        }
        var cfg = new JsonObject
        {
            ["server"] = pos[0], ["vault"] = pos[1], ["user"] = windows ? null : pos[2], ["password"] = password, ["windowsAuth"] = windows,
        };
        var report = new StringBuilder();
        void W(string s) { report.AppendLine(s); Console.WriteLine(s); }
        int rc = 0;
        try { RunAsync(cfg, W, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) { W(""); W("STOPPED: " + e.GetType().Name + ": " + e.Message); rc = 1; }
        File.WriteAllText(outPath, report.ToString().Replace(password ?? "\u0000", "********"));
        Console.WriteLine();
        Console.WriteLine($"Report written to {outPath}");
        return rc;
    }

    static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(true);
            if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
    }

    static string Clip(string s, int n = 3000) => s.Length > n ? s[..n] + $"… ({s.Length:N0} chars)" : s;

    public static async Task RunAsync(JsonObject config, Action<string> W, CancellationToken ct, Func<VaultConnection, VaultClient>? connect = null, ISidResolver? sids = null)
    {
        var cfg = VaultSource.Cfg.From(config);
        W($"Silo Vault probe {typeof(VaultProbe).Assembly.GetName().Version} · {DateTime.Now:yyyy-MM-dd HH:mm} · server {cfg.Connection.Server} · vault {cfg.Connection.Vault}");
        using var vc = (connect ?? (c => new VaultClient(c)))(cfg.Connection);
        vc.Trace = s => W("  " + s);
        W(""); W("== Web services");
        var v = await vc.FindServiceVersionAsync(ct);
        foreach (var name in new[] { "AuthService", "WinAuthService", "DocumentService", "PropertyService", "SecurityService", "AdminService", "FilestoreService" })
        {
            try
            {
                var s = await vc.ServiceAsync(name, ct);
                W($"{name}: {s.Url} ({(s.Soap12 ? "SOAP 1.2" : "SOAP 1.1")}, {s.Operations.Count()} operations)");
                foreach (var op in Interesting(name, s)) W($"    {op}({string.Join(", ", s.ParamsOf(op))})");
            }
            catch (Exception e) { W($"{name}: NOT FOUND ({e.Message})"); }
        }

        W(""); W("== Sign-in");
        await vc.SignInAsync(ct);

        W(""); W("== Folders");
        var root = await vc.GetFolderRootAsync(ct);
        var folders = await vc.GetFoldersAsync(root.Id, true, ct);
        W($"Root {root.FullName} (id {root.Id}); {folders.Count:N0} folders below it, {folders.Count(f => f.Cloaked):N0} cloaked.");
        foreach (var f in folders.Take(15)) W($"    {f.Id,8}  {f.FullName}{(f.Cloaked ? "  [cloaked]" : "")}");
        var raw = await vc.CallAsync("DocumentService", "GetFoldersByParentId", new() { ["parentFolderId"] = root.Id, ["recurse"] = false }, ct);
        W("Raw folder record: " + Clip(raw.Result?.Elements().FirstOrDefault()?.ToString() ?? "(none)", 1500));

        W(""); W("== Files");
        var sample = new List<(VFolder folder, VFile file)>();
        XElementHolder? firstRaw = null;
        foreach (var f in new[] { root }.Concat(folders.Where(x => !x.Cloaked)))
        {
            var files = await vc.GetLatestFilesAsync(f.Id, false, ct);
            if (files.Count == 0) continue;
            if (firstRaw == null)
            {
                var r = await vc.CallAsync("DocumentService", "GetLatestFilesByFolderId", new() { ["folderId"] = f.Id, ["includeHidden"] = false }, ct);
                firstRaw = new XElementHolder(r.Result?.Elements().FirstOrDefault()?.ToString() ?? "");
            }
            foreach (var x in files.Take(Math.Max(0, 12 - sample.Count))) sample.Add((f, x));
            if (sample.Count >= 12) break;
        }
        W($"{sample.Count} sample files:");
        foreach (var (f, x) in sample)
            W($"    {f.FullName}/{x.Name}  id {x.Id} master {x.MasterId} v{x.VerNum} {x.Size:N0} B rev '{x.Revision}' state '{x.State}' released {x.Released} cat '{x.Category}' by {x.CreatedBy} {x.CheckedIn:yyyy-MM-dd}");
        if (firstRaw != null) W("Raw file record: " + Clip(firstRaw.Xml, 4000));
        var withState = sample.Select(s => s.file).FirstOrDefault(x => x.State != null && !x.Released);
        if (withState != null)
        {
            var versions = await vc.GetVersionsAsync(withState.MasterId, ct);
            W($"Versions of {withState.Name}: " + string.Join("; ", versions.Select(x => $"v{x.VerNum} {x.State}{(x.Released ? " (released)" : "")}")));
        }

        W(""); W("== Properties");
        var defs = await vc.GetFilePropertyDefinitionsAsync(ct);
        W($"{defs.Count} file property definitions: " + string.Join(", ", defs.Take(80).Select(d => d.DisplayName + (d.SystemName != null && d.SystemName != d.DisplayName ? $" [{d.SystemName}]" : ""))));
        if (sample.Count > 0)
        {
            var props = await vc.GetFilePropertiesAsync(sample.Take(3).Select(s => s.file.Id).ToList(), ct);
            var byId = defs.ToDictionary(d => d.Id);
            foreach (var g in props.GroupBy(p => p.EntityId))
                W($"    file {g.Key}: " + Clip(string.Join(" | ", g.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{byId.GetValueOrDefault(p.PropDefId)?.DisplayName ?? p.PropDefId.ToString()}={p.Value}")), 1500));
        }

        W(""); W("== Users and groups");
        var users = await vc.GetUsersAsync(ct);
        var groups = await vc.GetGroupsAsync(ct);
        W($"{users.Count} users ({string.Join(", ", users.GroupBy(u => u.Auth ?? "?").Select(g => $"{g.Count()} {g.Key}"))}), {groups.Count} groups ({string.Join(", ", groups.GroupBy(u => u.Auth ?? "?").Select(g => $"{g.Count()} {g.Key}"))}).");
        foreach (var u in users.Take(10)) W($"    user {u.Id,6}  {u.Name}  auth {u.Auth}  active {u.Active}{(u.IsSystem ? "  system" : "")}");
        foreach (var g in groups.Take(10)) W($"    group {g.Id,5}  {g.Name}  auth {g.Auth}  active {g.Active}");
        var dir = new VaultDirectory(users, groups);
        foreach (var g in groups.Where(g => g.Active).Take(30))
        {
            try { dir.Members[g.Id] = await vc.GetGroupMembersAsync(g.Id, ct); W($"    members of {g.Name}: {dir.Members[g.Id].users.Count} users, {dir.Members[g.Id].groups.Count} groups"); }
            catch (Exception e) { W($"    members of {g.Name}: FAILED {e.Message}"); }
        }

        W(""); W("== Permissions");
        var folderIds = new[] { root.Id }.Concat(folders.Take(20).Select(f => f.Id)).ToList();
        var fr = await vc.CallAsync("SecurityService", "GetEntACLsByEntityIds", new() { ["entityIds"] = folderIds }, ct);
        W("Raw folder security: " + Clip(fr.Result?.ToString() ?? "(none)", 5000));
        if (sample.Count > 0)
        {
            var ids = sample.Take(6).SelectMany(s => new[] { s.file.MasterId, s.file.Id }).Distinct().ToList();
            var r2 = await vc.CallAsync("SecurityService", "GetEntACLsByEntityIds", new() { ["entityIds"] = ids }, ct);
            W("Raw file security (master ids and file ids): " + Clip(r2.Result?.ToString() ?? "(none)", 5000));
        }

        W(""); W("== What Silo would do");
        var resolver = sids ?? new WindowsSidResolver();
        var mapper = new AclMapper(dir, cfg.Acl, resolver);
        foreach (var u in users.Where(u => u.Active).Take(10)) W($"    {u.Name} -> {mapper.UserSid(u.Id) ?? "no Windows account (no access in Silo)"}");
        W($"    Files with no ACL would be visible to {mapper.Everyone().Count} Windows accounts.");
        var source = new VaultSource(resolver, _ => vc);
        int shown = 0;
        try
        {
            var probeCfg = (JsonObject)config.DeepClone();
            await source.EnumerateAsync(new EnumerateArgs { SourceId = "probe", Config = probeCfg }, item =>
            {
                if (shown++ >= 12) return;
                W($"    {item.Location}: allow {item.Allow.Count} SIDs, deny {item.Deny.Count}{(item.Meta.TryGetValue("access_problem", out var p) ? " — " + p[0] : "")}; part '{First(item, "vault_part")}' title '{First(item, "title")}' state '{First(item, "vault_state")}' open {item.Url}");
            }, ct, keepSignedIn: true);
        }
        catch (Exception e) { W("    listing FAILED: " + e.Message); }

        W(""); W("== Download");
        var small = sample.Select(s => s.file).Where(f => f.Size is > 0 and < 20_000_000).OrderBy(f => f.Size).FirstOrDefault();
        if (small != null)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "silo-vault-probe-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(small.Name));
            try
            {
                await vc.DownloadAsync(small, tmp, ct);
                var got = new FileInfo(tmp).Length;
                var head = await File.ReadAllBytesAsync(tmp, ct);
                W($"{small.Name}: {got:N0} of {small.Size:N0} bytes{(got == small.Size ? " (OK)" : " (SIZE DIFFERS)")}; starts {Convert.ToHexString(head.AsSpan(0, Math.Min(16, head.Length)))}");
            }
            catch (Exception e) { W($"{small.Name}: download FAILED {e.Message}"); }
            finally { try { File.Delete(tmp); } catch { } }
        }
        else W("No small file to try.");
        await vc.SignOutAsync();
        W(""); W("Done. Nothing in Vault was changed.");
    }

    static string? First(SourceItem i, string k) => i.Meta.TryGetValue(k, out var l) && l.Count > 0 ? l[0] : null;

    static IEnumerable<string> Interesting(string service, SoapService s)
    {
        string[] want = service switch
        {
            "AuthService" or "WinAuthService" => ["SignIn", "SignInReadOnly", "SignOut"],
            "DocumentService" => ["GetFolderRoot", "GetFolderByPath", "GetFoldersByParentId", "GetLatestFilesByFolderId", "GetFilesByMasterId", "GetLatestFileByMasterId", "GetFileById", "GetDownloadTicketsByFileIds"],
            "PropertyService" => ["GetPropertyDefinitionsByEntityClassId", "GetPropertiesByEntityIds"],
            "SecurityService" => s.Operations.Where(o => o.Contains("ACL", StringComparison.OrdinalIgnoreCase) || o.Contains("Acl")).ToArray(),
            "AdminService" => ["GetAllUsers", "GetAllGroups", "GetGroupInfoByGroupId", "GetMemberUsersByGroupId", "GetMemberGroupsByGroupId"],
            "FilestoreService" => ["DownloadFilePart"],
            _ => [],
        };
        foreach (var w in want) yield return s.Has(w) ? w : w + " — MISSING";
    }

    sealed record XElementHolder(string Xml);
}
