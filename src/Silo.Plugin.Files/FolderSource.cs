using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Silo.Plugins.Files;

/// <summary>
/// Folder or SMB share source. Walks a folder tree, reports each file with its NTFS permissions (or "public"),
/// and picks up library metadata from sidecar files: an <c>item.json</c> in a folder describes every file in
/// that folder (the open-geo-library layout: WAMEX, GSQ, ...). Watches for changes with a folder watcher.
///
/// Config: root (required), access ("ntfs" | "public"), exclude (folder or file name patterns), sidecars (true).
/// </summary>
public sealed class FolderSource : SourcePlugin
{
    public override PluginManifest Manifest { get; } = new()
    {
        Id = "silo.source.folder",
        Name = "Folders and file shares",
        Version = "1.0.0",
        Kind = "source",
        Description = "Local folders and SMB shares, with NTFS permissions and item.json library metadata.",
        Publisher = "Silo",
        Concurrency = 1,
        TimeoutSec = 86_400,
        ConfigTemplate = new JsonObject
        {
            ["root"] = @"D:\PublicMiningData",
            ["access"] = "ntfs",
            ["exclude"] = new JsonArray("_state", "*.part", "*.tmp", "~$*", "Thumbs.db", "desktop.ini"),
            ["sidecars"] = true,
        },
    };

    static readonly string[] SidecarNames = ["item.json", "_meta.json", "silo.meta.json"];

    sealed class Cfg
    {
        public string Root = "";
        public bool Public;
        public bool Sidecars = true;
        public List<string> Exclude = new();
        public string? Subpath;
    }

    static Cfg Read(EnumerateArgs a)
    {
        var c = new Cfg();
        c.Root = a.Config["root"]?.GetValue<string>() ?? throw new InvalidOperationException("config.root is required");
        c.Public = string.Equals(a.Config["access"]?.GetValue<string>(), "public", StringComparison.OrdinalIgnoreCase);
        if (a.Config["sidecars"] is JsonValue sv && sv.TryGetValue<bool>(out var sc)) c.Sidecars = sc;
        if (a.Config["exclude"] is JsonArray ex) c.Exclude = ex.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList();
        else c.Exclude = ["_state", "*.part", "*.tmp", "~$*", "Thumbs.db", "desktop.ini"];
        c.Subpath = a.Config["subpath"]?.GetValue<string>();
        return c;
    }

    static bool Excluded(Cfg c, string name)
    {
        foreach (var p in c.Exclude)
            if (Glob(p, name)) return true;
        return SidecarNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    static bool Glob(string pattern, string name)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?')) return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
        return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);
    }

    public override EnumerateResult Enumerate(EnumerateArgs args, Action<SourceItem> emit, CancellationToken ct)
    {
        var c = Read(args);
        var start = c.Subpath == null ? c.Root : Path.Combine(c.Root, c.Subpath);
        if (!Directory.Exists(c.Root)) throw new DirectoryNotFoundException($"Folder not found or not reachable: {c.Root}");
        long n = 0;
        var stack = new Stack<string>();
        if (Directory.Exists(start)) stack.Push(start);
        else if (File.Exists(start)) { var it = Make(c, new FileInfo(start), SidecarFor(c, Path.GetDirectoryName(start)!)); if (it != null) { emit(it); n++; } }
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint, RecurseSubdirectories = false };
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            Sidecar? side = SidecarFor(c, dir);
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToList(); }
            catch (Exception) { continue; }
            foreach (var e in entries)
            {
                if (Excluded(c, e.Name)) continue;
                if (e is DirectoryInfo d) { stack.Push(d.FullName); continue; }
                if (e is FileInfo f)
                {
                    var item = Make(c, f, side);
                    if (item != null) { emit(item); n++; }
                }
            }
        }
        return new EnumerateResult { Count = n, Complete = c.Subpath == null, Checkpoint = DateTime.UtcNow.ToString("o") };
    }

    public override SourceItem? Get(EnumerateArgs args, string key)
    {
        var c = Read(args);
        var path = Path.Combine(c.Root, key);
        if (!File.Exists(path)) return null;
        return Make(c, new FileInfo(path), SidecarFor(c, Path.GetDirectoryName(path)!));
    }

    // ---------- sidecar metadata ----------

    sealed record Sidecar(DateTime Stamp, Dictionary<string, List<string>> Meta);

    readonly Dictionary<string, Sidecar?> _sideCache = new(StringComparer.OrdinalIgnoreCase);

    Sidecar? SidecarFor(Cfg c, string dir)
    {
        if (!c.Sidecars) return null;
        foreach (var name in SidecarNames)
        {
            var p = Path.Combine(dir, name);
            FileInfo fi;
            try { fi = new FileInfo(p); if (!fi.Exists) continue; } catch { continue; }
            var stamp = fi.LastWriteTimeUtc;
            lock (_sideCache)
                if (_sideCache.TryGetValue(p, out var hit) && hit != null && hit.Stamp == stamp) return hit;
            Sidecar? s = null;
            try { s = new Sidecar(stamp, ParseSidecar(File.ReadAllText(p))); } catch { }
            lock (_sideCache) { if (_sideCache.Count > 5000) _sideCache.Clear(); _sideCache[p] = s; }
            return s;
        }
        return null;
    }

    /// <summary>Maps an open-geo-library item.json (or any flat JSON object) to well-known metadata keys.</summary>
    public static Dictionary<string, List<string>> ParseSidecar(string json)
    {
        var meta = new Dictionary<string, List<string>>();
        var root = JsonNode.Parse(json.TrimStart('\uFEFF')) as JsonObject;
        if (root == null) return meta;
        foreach (var (k, v) in root)
        {
            if (v == null) continue;
            string key = k.ToLowerInvariant() switch
            {
                "id" => "collection",
                "title" => "collection_title",
                "type" => "report_type",
                "source" => "publisher",
                "source_id" => "dataset",
                "landing" => "landing_page",
                "licence_url" or "license_url" => "licence_url",
                "license" => "licence",
                "files" or "downloaded" => "",
                var other => other,
            };
            if (key.Length == 0) continue;
            if (v is JsonArray arr) { foreach (var x in arr) if (x is JsonValue xv) meta.Add(key, xv.ToString()); continue; }
            if (v is not JsonValue jv) continue;
            var s = jv.ToString();
            if (key == "commodity")
            {
                foreach (var part in s.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) meta.Add(key, part);
                continue;
            }
            if (key == "company" && s.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)) continue;
            meta.Add(key, s);
        }
        return meta;
    }

    // ---------- items ----------

    SourceItem? Make(Cfg c, FileInfo f, Sidecar? side)
    {
        try
        {
            var rel = Path.GetRelativePath(c.Root, f.FullName);
            var item = new SourceItem
            {
                Key = rel,
                Path = f.FullName,
                Name = f.Name,
                Ext = f.Extension.ToLowerInvariant(),
                Size = f.Length,
                Modified = f.LastWriteTimeUtc,
                Created = f.CreationTimeUtc,
                Location = f.DirectoryName,
                Version = $"{f.Length:x}-{f.LastWriteTimeUtc.Ticks:x}-{(side?.Stamp.Ticks ?? 0):x}",
            };
            if (side != null) item.Meta = side.Meta.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            if (item.Meta.TryGetValue("landing_page", out var lp) && lp.Count > 0) item.Url = lp[0];
            if (c.Public) item.Allow.Add("public");
            else Permissions(f, item);
            return item;
        }
        catch (Exception) { return null; }
    }

    // Security descriptors repeat across thousands of files: decode each distinct one once.
    readonly Dictionary<string, (List<string> allow, List<string> deny)> _aclCache = new();

    void Permissions(FileInfo f, SourceItem item)
    {
        if (!OperatingSystem.IsWindows()) return;  // no ACLs to read: nobody sees it (fail closed)
        try
        {
            var sec = f.GetAccessControl(AccessControlSections.Access);
            var sddl = sec.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            (List<string> allow, List<string> deny) v;
            lock (_aclCache)
            {
                if (!_aclCache.TryGetValue(sddl, out v))
                {
                    v = (new List<string>(), new List<string>());
                    foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    {
                        if ((r.FileSystemRights & FileSystemRights.ReadData) == 0) continue;
                        var sid = r.IdentityReference.Value;
                        var list = r.AccessControlType == AccessControlType.Allow ? v.allow : v.deny;
                        if (!list.Contains(sid)) list.Add(sid);
                    }
                    if (_aclCache.Count > 20000) _aclCache.Clear();
                    _aclCache[sddl] = v;
                }
            }
            item.Allow.AddRange(v.allow);
            item.Deny.AddRange(v.deny);
        }
        catch (Exception) { /* unreadable ACL: leave empty, which hides the item */ }
    }

    // ---------- watching ----------

    FileSystemWatcher? _watcher;
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    Timer? _flush;

    public override bool Watch(EnumerateArgs args, Action<IReadOnlyList<string>> changed)
    {
        var c = Read(args);
        if (!Directory.Exists(c.Root)) return false;
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(c.Root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Security,
        };
        void Note(string full)
        {
            string rel;
            try { rel = Path.GetRelativePath(c.Root, full); } catch { return; }
            var name = Path.GetFileName(full);
            if (SidecarNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                rel = (Path.GetDirectoryName(rel) is { Length: > 0 } d ? d : ".") + Path.DirectorySeparatorChar + "*";
            else if (Excluded(c, name) || rel.Split(Path.DirectorySeparatorChar).Any(seg => Excluded(c, seg))) return;
            else if (Directory.Exists(full)) rel = rel + Path.DirectorySeparatorChar + "*";
            lock (_pending) _pending.Add(rel);
            _flush!.Change(3000, Timeout.Infinite);
        }
        _flush = new Timer(_ =>
        {
            List<string> keys;
            lock (_pending) { keys = _pending.ToList(); _pending.Clear(); }
            if (keys.Count > 0) changed(keys);
        });
        _watcher.Created += (_, e) => Note(e.FullPath);
        _watcher.Changed += (_, e) => Note(e.FullPath);
        _watcher.Deleted += (_, e) => Note(e.FullPath);
        _watcher.Renamed += (_, e) => { Note(e.OldFullPath); Note(e.FullPath); };
        _watcher.Error += (_, _) => changed(["*"]);   // buffer overflow: ask for a full rescan
        _watcher.EnableRaisingEvents = true;
        return true;
    }
}
