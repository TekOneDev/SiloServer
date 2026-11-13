using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Silo.Plugins;

namespace Silo.Server.Core;

public static class ItemState
{
    public const int Pending = 0;      // new or changed: needs extraction
    public const int NeedsOcr = 2;     // text extracted, some pages need OCR
    public const int Done = 3;
    public const int MetadataOnly = 4; // no filter, too big, or skipped
    public const int Failed = 9;
}

public sealed class ItemRow
{
    public long Id;
    public string SourceId = "";
    public string Key = "";
    public string Version = "";
    public string? Path;
    public string Name = "";
    public string? Ext;
    public long Size;
    public long? Modified, Created;
    public string? Location, Url;
    public List<string> Allow = new(), Deny = new();
    public Dictionary<string, List<string>> Meta = new();
    public int State;
    public string? Error, Filter, Title, Author, Summary, TextHash;
    public Dictionary<string, List<string>> Fields = new();
    public List<Entity> Entities = new();
    public Geo? Geo;
    public int Pages, OcrPages, OcrWanted;
    public long? ExtractedAt;
    public string? Text;   // inline text from the source (no filter)

    public string? M(string key) => Meta.TryGetValue(key, out var l) && l.Count > 0 ? l[0] : null;
    public string Dataset => M("dataset") ?? SourceId;
    public string? Collection => M("collection");
}

/// <summary>
/// The catalog: the record of truth for every item, its extracted text (compressed) and enrichment cache.
/// The search index can always be rebuilt from here without reading a file again. SQLite in WAL mode;
/// one writer connection behind a lock, a fresh reader connection per read.
/// </summary>
public sealed class Catalog : IDisposable
{
    const int SchemaVersion = 1;
    static readonly JsonSerializerOptions J = Contract.Json;
    readonly string _cs;
    readonly SqliteConnection _w;
    readonly object _wl = new();

    public Catalog(string file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = file, Cache = SqliteCacheMode.Private, Pooling = true }.ToString();
        _w = new SqliteConnection(_cs);
        _w.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA mmap_size=1073741824; PRAGMA cache_size=-200000; PRAGMA busy_timeout=10000;");
        Exec("""
            CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);
            CREATE TABLE IF NOT EXISTS items(
              id INTEGER PRIMARY KEY, source_id TEXT NOT NULL, key TEXT NOT NULL, version TEXT NOT NULL, gen INTEGER NOT NULL DEFAULT 0,
              path TEXT, name TEXT NOT NULL, ext TEXT, size INTEGER NOT NULL DEFAULT 0, modified INTEGER, created INTEGER,
              location TEXT, url TEXT, allow TEXT, deny TEXT, meta TEXT, state INTEGER NOT NULL DEFAULT 0, error TEXT,
              filter TEXT, title TEXT, author TEXT, summary TEXT, fields TEXT, entities TEXT, geo TEXT,
              pages INTEGER NOT NULL DEFAULT 0, ocr_pages INTEGER NOT NULL DEFAULT 0, ocr_wanted INTEGER NOT NULL DEFAULT 0,
              text_hash TEXT, extracted_at INTEGER, inline_text TEXT,
              UNIQUE(source_id, key));
            CREATE INDEX IF NOT EXISTS items_state ON items(state);
            CREATE INDEX IF NOT EXISTS items_coll ON items(json_extract(meta, '$.dataset[0]'), json_extract(meta, '$.collection[0]'));
            CREATE TABLE IF NOT EXISTS content(item_id INTEGER PRIMARY KEY, data BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS dirty(seq INTEGER PRIMARY KEY AUTOINCREMENT, item_id INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS enrich(dataset TEXT NOT NULL, collection TEXT NOT NULL, plugin TEXT NOT NULL, data TEXT, final INTEGER NOT NULL, fetched INTEGER NOT NULL,
              PRIMARY KEY(dataset, collection, plugin));
            CREATE TABLE IF NOT EXISTS sources(id TEXT PRIMARY KEY, checkpoint TEXT, gen INTEGER NOT NULL DEFAULT 0, last_scan INTEGER, last_count INTEGER, status TEXT);
            """);
        if (Get("schema") == null) Set("schema", SchemaVersion.ToString());
    }

    SqliteConnection Reader() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    void Exec(string sql) { lock (_wl) { using var c = _w.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); } }

    public string? Get(string k)
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k=$k"; cmd.Parameters.AddWithValue("$k", k);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string k, string v)
    {
        lock (_wl) { using var cmd = _w.CreateCommand(); cmd.CommandText = "INSERT INTO meta(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=$v"; cmd.Parameters.AddWithValue("$k", k); cmd.Parameters.AddWithValue("$v", v); cmd.ExecuteNonQuery(); }
    }

    static string? Js<T>(T? v) => v == null ? null : JsonSerializer.Serialize(v, J);
    static T? Jd<T>(string? s) where T : class => string.IsNullOrEmpty(s) ? null : JsonSerializer.Deserialize<T>(s, J);
    static long? Ms(DateTime? d) => d == null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, d.Value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : d.Value.Kind)).ToUnixTimeMilliseconds();
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---------- sources ----------

    public int NextGeneration(string sourceId)
    {
        lock (_wl)
        {
            using var cmd = _w.CreateCommand();
            cmd.CommandText = "INSERT INTO sources(id, gen) VALUES($id, 1) ON CONFLICT(id) DO UPDATE SET gen=gen+1 RETURNING gen";
            cmd.Parameters.AddWithValue("$id", sourceId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void SourceScanned(string sourceId, string? checkpoint, long count, string status)
    {
        lock (_wl)
        {
            using var cmd = _w.CreateCommand();
            cmd.CommandText = "UPDATE sources SET checkpoint=$c, last_scan=$t, last_count=$n, status=$s WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", sourceId); cmd.Parameters.AddWithValue("$c", (object?)checkpoint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", Now); cmd.Parameters.AddWithValue("$n", count); cmd.Parameters.AddWithValue("$s", status);
            cmd.ExecuteNonQuery();
        }
    }

    public void SourceStatus(string sourceId, string status)
    {
        lock (_wl)
        {
            using var cmd = _w.CreateCommand();
            cmd.CommandText = "INSERT INTO sources(id, status) VALUES($id,$s) ON CONFLICT(id) DO UPDATE SET status=$s";
            cmd.Parameters.AddWithValue("$id", sourceId); cmd.Parameters.AddWithValue("$s", status);
            cmd.ExecuteNonQuery();
        }
    }

    public (string? checkpoint, long? lastScan, long? count, string? status) SourceInfo(string sourceId)
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT checkpoint, last_scan, last_count, status FROM sources WHERE id=$id"; cmd.Parameters.AddWithValue("$id", sourceId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null, null, null);
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3));
    }

    // ---------- items from sources ----------

    /// <summary>Adds or updates items. New items and changed versions go back to Pending. Returns how many changed.</summary>
    public int Upsert(string sourceId, int gen, IReadOnlyList<SourceItem> items)
    {
        if (items.Count == 0) return 0;
        int changed = 0;
        lock (_wl)
        {
            using var tx = _w.BeginTransaction();
            using var sel = _w.CreateCommand();
            sel.CommandText = "SELECT id, version FROM items WHERE source_id=$s AND key=$k";
            var ps = sel.Parameters.Add("$s", SqliteType.Text); var pk = sel.Parameters.Add("$k", SqliteType.Text);
            using var touch = _w.CreateCommand();
            touch.CommandText = "UPDATE items SET gen=$g WHERE id=$id";
            var tg = touch.Parameters.Add("$g", SqliteType.Integer); var tid = touch.Parameters.Add("$id", SqliteType.Integer);
            using var up = _w.CreateCommand();
            up.CommandText = """
                INSERT INTO items(source_id, key, version, gen, path, name, ext, size, modified, created, location, url, allow, deny, meta, state, inline_text)
                VALUES($s,$k,$v,$g,$path,$name,$ext,$size,$mod,$cre,$loc,$url,$allow,$deny,$meta,0,$text)
                ON CONFLICT(source_id, key) DO UPDATE SET version=$v, gen=$g, path=$path, name=$name, ext=$ext, size=$size, modified=$mod, created=$cre,
                  location=$loc, url=$url, allow=$allow, deny=$deny, meta=$meta, inline_text=$text,
                  state=CASE WHEN items.size=$size AND items.modified IS $mod AND items.state<>9 THEN items.state ELSE 0 END
                RETURNING id
                """;
            string[] names = ["$s", "$k", "$v", "$g", "$path", "$name", "$ext", "$size", "$mod", "$cre", "$loc", "$url", "$allow", "$deny", "$meta", "$text"];
            var p = names.ToDictionary(n => n, n => up.Parameters.Add(n, SqliteType.Text));
            using var dirty = _w.CreateCommand();
            dirty.CommandText = "INSERT INTO dirty(item_id) VALUES($id)";
            var did = dirty.Parameters.Add("$id", SqliteType.Integer);
            foreach (var it in items)
            {
                ps.Value = sourceId; pk.Value = it.Key;
                long? id = null; string? ver = null;
                using (var r = sel.ExecuteReader()) if (r.Read()) { id = r.GetInt64(0); ver = r.GetString(1); }
                if (id != null && ver == it.Version) { tg.Value = gen; tid.Value = id; touch.ExecuteNonQuery(); continue; }
                p["$s"].Value = sourceId; p["$k"].Value = it.Key; p["$v"].Value = it.Version; p["$g"].Value = gen;
                p["$path"].Value = (object?)it.Path ?? DBNull.Value; p["$name"].Value = it.Name; p["$ext"].Value = (object?)it.Ext ?? DBNull.Value;
                p["$size"].Value = it.Size; p["$mod"].Value = (object?)Ms(it.Modified) ?? DBNull.Value; p["$cre"].Value = (object?)Ms(it.Created) ?? DBNull.Value;
                p["$loc"].Value = (object?)it.Location ?? DBNull.Value; p["$url"].Value = (object?)it.Url ?? DBNull.Value;
                p["$allow"].Value = Js(it.Allow); p["$deny"].Value = Js(it.Deny); p["$meta"].Value = Js(it.Meta); p["$text"].Value = (object?)it.Text ?? DBNull.Value;
                var newId = Convert.ToInt64(up.ExecuteScalar());
                did.Value = newId; dirty.ExecuteNonQuery();   // metadata changes show in search straight away
                changed++;
            }
            tx.Commit();
        }
        return changed;
    }

    /// <summary>Removes items of a source that a complete scan did not see. Returns how many.</summary>
    public int RemoveUnseen(string sourceId, int gen) => RemoveWhere("source_id=$s AND gen<$g", ("$s", sourceId), ("$g", gen));

    public int RemoveKey(string sourceId, string key) => RemoveWhere("source_id=$s AND key=$k", ("$s", sourceId), ("$k", key));

    /// <summary>Removes items under a folder key prefix that were not seen in a partial rescan of that folder.</summary>
    public int RemoveUnseenUnder(string sourceId, string prefix, int gen) =>
        RemoveWhere("source_id=$s AND gen<$g AND (key LIKE $p ESCAPE '\\')", ("$s", sourceId), ("$g", gen), ("$p", prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));

    public int RemoveSource(string sourceId) => RemoveWhere("source_id=$s", ("$s", sourceId));

    int RemoveWhere(string where, params (string, object)[] args)
    {
        lock (_wl)
        {
            using var tx = _w.BeginTransaction();
            using var cmd = _w.CreateCommand();
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
            cmd.CommandText = $"INSERT INTO dirty(item_id) SELECT id FROM items WHERE {where}";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DELETE FROM content WHERE item_id IN (SELECT id FROM items WHERE {where})";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DELETE FROM items WHERE {where}";
            int n = cmd.ExecuteNonQuery();
            tx.Commit();
            return n;
        }
    }

    // ---------- work queues ----------

    public List<long> NextIds(int state, int n, ICollection<long> skip)
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM items WHERE state=$st ORDER BY id LIMIT $n";
        cmd.Parameters.AddWithValue("$st", state); cmd.Parameters.AddWithValue("$n", n + skip.Count);
        var list = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read() && list.Count < n) { var id = r.GetInt64(0); if (!skip.Contains(id)) list.Add(id); }
        return list;
    }

    public long Count(string where = "1=1")
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items WHERE " + where;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public Dictionary<int, long> CountByState()
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT state, COUNT(*) FROM items GROUP BY state";
        var d = new Dictionary<int, long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetInt32(0)] = r.GetInt64(1);
        return d;
    }

    public List<(string source, string ext, long n, long bytes)> CountByType()
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT source_id, IFNULL(ext,''), COUNT(*), SUM(size) FROM items GROUP BY source_id, ext ORDER BY 3 DESC LIMIT 200";
        var l = new List<(string, string, long, long)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) l.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? 0 : r.GetInt64(3)));
        return l;
    }

    public List<(long id, string name, string? error)> RecentErrors(int n)
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, error FROM items WHERE error IS NOT NULL ORDER BY extracted_at DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", n);
        var l = new List<(long, string, string?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) l.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return l;
    }

    // ---------- loading ----------

    const string Cols = "id, source_id, key, version, path, name, ext, size, modified, created, location, url, allow, deny, meta, state, error, filter, title, author, summary, fields, entities, geo, pages, ocr_pages, ocr_wanted, text_hash, extracted_at, inline_text";

    static ItemRow ReadRow(SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        long? L(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        return new ItemRow
        {
            Id = r.GetInt64(0), SourceId = r.GetString(1), Key = r.GetString(2), Version = r.GetString(3), Path = S(4), Name = r.GetString(5), Ext = S(6),
            Size = r.GetInt64(7), Modified = L(8), Created = L(9), Location = S(10), Url = S(11),
            Allow = Jd<List<string>>(S(12)) ?? new(), Deny = Jd<List<string>>(S(13)) ?? new(), Meta = Jd<Dictionary<string, List<string>>>(S(14)) ?? new(),
            State = r.GetInt32(15), Error = S(16), Filter = S(17), Title = S(18), Author = S(19), Summary = S(20),
            Fields = Jd<Dictionary<string, List<string>>>(S(21)) ?? new(), Entities = Jd<List<Entity>>(S(22)) ?? new(), Geo = Jd<Geo>(S(23)),
            Pages = r.GetInt32(24), OcrPages = r.GetInt32(25), OcrWanted = r.GetInt32(26), TextHash = S(27), ExtractedAt = L(28), Text = S(29),
        };
    }

    public ItemRow? Load(long id) => Load([id]).GetValueOrDefault(id);

    public Dictionary<long, ItemRow> Load(IReadOnlyCollection<long> ids)
    {
        var d = new Dictionary<long, ItemRow>();
        if (ids.Count == 0) return d;
        using var c = Reader();
        foreach (var chunk in ids.Chunk(500))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM items WHERE id IN ({string.Join(",", chunk)})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) { var row = ReadRow(r); d[row.Id] = row; }
        }
        return d;
    }

    public List<ItemRow> LoadCollection(string dataset, string collection, int max = 200)
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM items WHERE json_extract(meta, '$.dataset[0]')=$d AND json_extract(meta, '$.collection[0]')=$c ORDER BY name LIMIT $n";
        cmd.Parameters.AddWithValue("$d", dataset); cmd.Parameters.AddWithValue("$c", collection); cmd.Parameters.AddWithValue("$n", max);
        var l = new List<ItemRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) l.Add(ReadRow(r));
        return l;
    }

    // ---------- extraction results ----------

    public void SaveExtract(long id, int state, string? filter, ExtractResult? x, List<PageText>? pages, string? error, int ocrWanted, string? textHash)
    {
        lock (_wl)
        {
            using var tx = _w.BeginTransaction();
            using var cmd = _w.CreateCommand();
            cmd.CommandText = """
                UPDATE items SET state=$st, filter=$f, title=$t, author=$a, summary=$sum, fields=$fl, entities=$en, geo=$geo, pages=$pg,
                  ocr_pages=$op, ocr_wanted=$ow, text_hash=$th, extracted_at=$at, error=$err WHERE id=$id
                """;
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$st", state);
            cmd.Parameters.AddWithValue("$f", (object?)filter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", (object?)x?.Title ?? DBNull.Value); cmd.Parameters.AddWithValue("$a", (object?)x?.Author ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sum", (object?)x?.Summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fl", (object?)Js(x?.Fields) ?? DBNull.Value); cmd.Parameters.AddWithValue("$en", (object?)Js(x?.Entities) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$geo", (object?)Js(x?.Geo) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pg", pages?.Count ?? 0); cmd.Parameters.AddWithValue("$op", pages?.Count(p => p.Ocr) ?? 0);
            cmd.Parameters.AddWithValue("$ow", ocrWanted); cmd.Parameters.AddWithValue("$th", (object?)textHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", Now); cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            if (pages != null)
            {
                using var ct = _w.CreateCommand();
                ct.CommandText = "INSERT INTO content(item_id, data) VALUES($id,$d) ON CONFLICT(item_id) DO UPDATE SET data=$d";
                ct.Parameters.AddWithValue("$id", id); ct.Parameters.AddWithValue("$d", Pack(pages));
                ct.ExecuteNonQuery();
            }
            using var d = _w.CreateCommand();
            d.CommandText = "INSERT INTO dirty(item_id) VALUES($id)"; d.Parameters.AddWithValue("$id", id); d.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public void SaveOcr(long id, List<PageText> pages, string? textHash, string? error)
    {
        lock (_wl)
        {
            using var tx = _w.BeginTransaction();
            using var cmd = _w.CreateCommand();
            cmd.CommandText = "UPDATE items SET state=3, ocr_pages=$op, ocr_wanted=0, text_hash=IFNULL($th, text_hash), error=$err WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$op", pages.Count(p => p.Ocr));
            cmd.Parameters.AddWithValue("$th", (object?)textHash ?? DBNull.Value); cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            using var ct = _w.CreateCommand();
            ct.CommandText = "INSERT INTO content(item_id, data) VALUES($id,$d) ON CONFLICT(item_id) DO UPDATE SET data=$d";
            ct.Parameters.AddWithValue("$id", id); ct.Parameters.AddWithValue("$d", Pack(pages));
            ct.ExecuteNonQuery();
            using var d = _w.CreateCommand();
            d.CommandText = "INSERT INTO dirty(item_id) VALUES($id)"; d.Parameters.AddWithValue("$id", id); d.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public void SetState(long id, int state, string? error = null)
    {
        lock (_wl)
        {
            using var cmd = _w.CreateCommand();
            cmd.CommandText = "UPDATE items SET state=$st, error=$e WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$st", state); cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Puts items back in the extraction queue (e.g. after a filter plugin was added or upgraded).</summary>
    public int Requeue(string where)
    {
        lock (_wl) { using var cmd = _w.CreateCommand(); cmd.CommandText = "UPDATE items SET state=0 WHERE " + where; return cmd.ExecuteNonQuery(); }
    }

    static byte[] Pack(List<PageText> pages)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(pages, J);
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionLevel.Fastest, true)) br.Write(json);
        return ms.ToArray();
    }

    public List<PageText> Pages(long id)
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM content WHERE item_id=$id"; cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteScalar() is not byte[] b) return new();
        using var br = new BrotliStream(new MemoryStream(b), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<PageText>>(br, J) ?? new();
    }

    // ---------- index change log ----------

    /// <summary>The oldest pending changes after sequence number <paramref name="after"/> (those up to it are in the index writer, waiting for a commit).</summary>
    public (long maxSeq, List<long> ids) NextDirty(int n, long after = 0)
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT seq, item_id FROM dirty WHERE seq > $a ORDER BY seq LIMIT $n"; cmd.Parameters.AddWithValue("$n", n); cmd.Parameters.AddWithValue("$a", after);
        long max = 0; var ids = new HashSet<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) { max = r.GetInt64(0); ids.Add(r.GetInt64(1)); }
        return (max, ids.ToList());
    }

    public void ClearDirty(long maxSeq)
    {
        lock (_wl) { using var cmd = _w.CreateCommand(); cmd.CommandText = "DELETE FROM dirty WHERE seq<=$m"; cmd.Parameters.AddWithValue("$m", maxSeq); cmd.ExecuteNonQuery(); }
    }

    public long DirtyCount()
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dirty";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void MarkDirty(string where)
    {
        lock (_wl) { using var cmd = _w.CreateCommand(); cmd.CommandText = "INSERT INTO dirty(item_id) SELECT id FROM items WHERE " + where; cmd.ExecuteNonQuery(); }
    }

    public void MarkCollectionDirty(string dataset, string collection)
    {
        lock (_wl)
        {
            using var cmd = _w.CreateCommand();
            cmd.CommandText = "INSERT INTO dirty(item_id) SELECT id FROM items WHERE json_extract(meta, '$.dataset[0]')=$d AND json_extract(meta, '$.collection[0]')=$c";
            cmd.Parameters.AddWithValue("$d", dataset); cmd.Parameters.AddWithValue("$c", collection);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------- enrichment cache ----------

    /// <summary>Collections of these datasets that this plugin has not answered for (or failed to, more than a day ago).</summary>
    public List<(string dataset, string collection, Dictionary<string, List<string>> meta)> CollectionsToEnrich(string plugin, IReadOnlyCollection<string> datasets, int n)
    {
        if (datasets.Count == 0) return new();
        using var c = Reader(); using var cmd = c.CreateCommand();
        var inList = string.Join(",", datasets.Select((_, i) => "$d" + i));
        cmd.CommandText = $"""
            SELECT json_extract(i.meta, '$.dataset[0]') AS ds, json_extract(i.meta, '$.collection[0]') AS col, MIN(i.meta)
            FROM items i
            WHERE ds IN ({inList}) AND col IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM enrich e WHERE e.dataset=ds AND e.collection=col AND e.plugin=$p AND (e.final=1 OR e.fetched>$retry))
            GROUP BY ds, col LIMIT $n
            """;
        int k = 0; foreach (var d in datasets) cmd.Parameters.AddWithValue("$d" + k++, d);
        cmd.Parameters.AddWithValue("$p", plugin); cmd.Parameters.AddWithValue("$n", n);
        cmd.Parameters.AddWithValue("$retry", Now - 24 * 3600_000L);
        var l = new List<(string, string, Dictionary<string, List<string>>)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) l.Add((r.GetString(0), r.GetString(1), Jd<Dictionary<string, List<string>>>(r.GetString(2)) ?? new()));
        return l;
    }

    public void SaveEnrich(string plugin, EnrichOutput o)
    {
        lock (_wl)
        {
            using var cmd = _w.CreateCommand();
            cmd.CommandText = "INSERT INTO enrich(dataset, collection, plugin, data, final, fetched) VALUES($d,$c,$p,$data,$f,$t) ON CONFLICT(dataset, collection, plugin) DO UPDATE SET data=$data, final=$f, fetched=$t";
            cmd.Parameters.AddWithValue("$d", o.Dataset); cmd.Parameters.AddWithValue("$c", o.Collection); cmd.Parameters.AddWithValue("$p", plugin);
            cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(o, J)); cmd.Parameters.AddWithValue("$f", o.Final ? 1 : 0); cmd.Parameters.AddWithValue("$t", Now);
            cmd.ExecuteNonQuery();
        }
    }

    public List<EnrichOutput> Enrichments(string dataset, string collection)
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM enrich WHERE dataset=$d AND collection=$c AND data IS NOT NULL";
        cmd.Parameters.AddWithValue("$d", dataset); cmd.Parameters.AddWithValue("$c", collection);
        var l = new List<EnrichOutput>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (Jd<EnrichOutput>(r.GetString(0)) is { } o) l.Add(o);
        return l;
    }

    public long EnrichCount(bool withGeo)
    {
        using var c = Reader(); using var cmd = c.CreateCommand();
        cmd.CommandText = withGeo ? "SELECT COUNT(*) FROM enrich WHERE json_extract(data, '$.geo') IS NOT NULL" : "SELECT COUNT(*) FROM enrich";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Checkpoint() { try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch { } }

    public void Dispose() { Checkpoint(); _w.Dispose(); }
}
