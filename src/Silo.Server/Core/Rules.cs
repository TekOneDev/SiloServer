using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Silo.Plugins;

namespace Silo.Server.Core;

/// <summary>The rules file (rules.json): normalisation and extraction that spans every source.</summary>
public sealed class RulesFile
{
    public Dictionary<string, DatasetInfo> Datasets { get; set; } = new();
    public Dictionary<string, List<string>> Commodities { get; set; } = new();
    public int CommodityTextMinMentions { get; set; } = 6;
    public List<ReportTypeRule> ReportTypes { get; set; } = new();
    public List<TenementRule> Tenements { get; set; } = new();
    public int TenementTextMinMentions { get; set; } = 2;
    public Dictionary<string, List<string>> Lithology { get; set; } = new();
    public int LithologyTextMinMentions { get; set; } = 3;
    public Dictionary<string, List<string>> Methods { get; set; } = new();
    public int MethodTextMinMentions { get; set; } = 2;
    public List<List<string>> Synonyms { get; set; } = new();
}

public sealed class DatasetInfo { public string Label { get; set; } = ""; public string? Long { get; set; } public string? State { get; set; } }
public sealed class ReportTypeRule { public string Label { get; set; } = ""; public List<string> Match { get; set; } = new(); }
public sealed class TenementRule { public string Region { get; set; } = ""; public string Pattern { get; set; } = ""; public string Format { get; set; } = ""; public bool Upper { get; set; } = true; }

/// <summary>What the rules derive for one item at index time.</summary>
public sealed class Derived
{
    public string DisplayTitle = "";
    public string? Subtitle;
    public List<string> Commodities = new();
    public string? ReportType;
    public List<string> Tenements = new();
    public List<string> Lithology = new();
    public List<string> Methods = new();
    public List<string> Companies = new();
    public string? State;
    public string DatasetLabel = "";
    public int? Year;
    public Geo? Geo;
    public Dictionary<string, List<string>> Lookup = new();
}

/// <summary>
/// The core's cross-source rules: canonical commodities, report types and tenement ids, rock types and
/// methods found in text, locations from text, and CSV lookups (e.g. ASX companies by report).
/// Rules run at index time, so a change to rules.json needs a reindex, not a re-extraction.
/// </summary>
public sealed class Rules
{
    public RulesFile File { get; private set; } = new();
    readonly SettingsStore _settings;
    readonly Log _log;
    Dictionary<string, string> _commodityAlias = new(StringComparer.OrdinalIgnoreCase);
    List<(string canon, Regex rx)> _commodityText = new(), _lith = new(), _methods = new();
    List<(TenementRule rule, Regex rx)> _tenements = new();
    public Dictionary<string, List<string>> SynonymMap { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    readonly CsvLookups _lookups;

    public Rules(SettingsStore settings, Log log, Catalog? catalog)
    {
        _settings = settings; _log = log;
        _lookups = new CsvLookups(settings, log, catalog);
        Load();
    }

    public string Path
    {
        get
        {
            var custom = System.IO.Path.Combine(_settings.Root, "rules.json");
            return System.IO.File.Exists(custom) ? custom : System.IO.Path.Combine(_settings.BinDir, "rules.json");
        }
    }

    public string Fingerprint { get; private set; } = "";

    public void Load()
    {
        var path = Path;
        RulesFile f;
        try
        {
            var text = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "{}";
            f = JsonSerializer.Deserialize<RulesFile>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new();
            Fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..12];
        }
        catch (Exception e) { _log.Error($"rules.json could not be read ({e.Message}); using no rules"); f = new(); }
        File = f;
        var alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text1 = new List<(string, Regex)>();
        foreach (var (canon, names) in f.Commodities)
        {
            alias[canon] = canon;
            foreach (var n in names) alias.TryAdd(n, canon);
            // In free text only distinctive names count (not "au", "w", "lead" as a verb is a risk but accepted).
            var words = names.Append(canon).Where(n => n.Length >= 4 && n is not ("lead" or "iron" or "water" or "salt" or "lime" or "clay" or "silica" or "oil" or "gas")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (words.Count > 0) text1.Add((canon, WordsRx(words)));
        }
        _commodityAlias = alias; _commodityText = text1;
        _lith = f.Lithology.Select(kv => (kv.Key, WordsRx(kv.Value))).ToList();
        _methods = f.Methods.Select(kv => (kv.Key, WordsRx(kv.Value))).ToList();
        _tenements = f.Tenements.Select(t => (t, new Regex(t.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase))).ToList();
        var syn = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in f.Synonyms)
            foreach (var w in g) syn[w] = g.Where(x => !x.Equals(w, StringComparison.OrdinalIgnoreCase)).ToList();
        SynonymMap = syn;
    }

    static Regex WordsRx(IEnumerable<string> words) =>
        new(@"\b(?:" + string.Join("|", words.OrderByDescending(w => w.Length).Select(w => Regex.Escape(w).Replace("\\ ", @"[\s-]+"))) + @")\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string? CanonCommodity(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return null;
        if (_commodityAlias.TryGetValue(s, out var c)) return c;
        // "GOLD - ALLUVIAL", "copper ore": first known word wins.
        foreach (var part in Regex.Split(s, @"[\s,/\-()]+"))
            if (part.Length >= 3 && _commodityAlias.TryGetValue(part, out c)) return c;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }

    public string DatasetLabel(string dataset) => File.Datasets.TryGetValue(dataset, out var d) && d.Label.Length > 0 ? d.Label : dataset;

    public string? ReportType(string? raw, string? title)
    {
        foreach (var src in new[] { raw, title })
        {
            if (string.IsNullOrWhiteSpace(src)) continue;
            var s = src.ToLowerInvariant();
            foreach (var rt in File.ReportTypes)
                if (rt.Match.Any(m => m.Length == 0 ? src == raw : s.Contains(m))) return rt.Label;
        }
        return null;
    }

    public IEnumerable<string> FindTenements(string text)
    {
        foreach (var (rule, rx) in _tenements)
            foreach (Match m in rx.Matches(text))
            {
                var s = rule.Format;
                for (int g = 1; g < m.Groups.Count; g++)
                {
                    var v = m.Groups[g].Value;
                    bool digits = v.Length > 0 && v.All(char.IsDigit);
                    if (digits) v = long.Parse(v).ToString();
                    // "{2:00}" pads a number group (WA mining districts are written E 08/1622).
                    if (digits) s = s.Replace("{" + g + ":00}", long.Parse(v).ToString("00"));
                    s = s.Replace("{" + g + "}", v);
                }
                yield return rule.Upper ? s.ToUpperInvariant() : s;
            }
    }

    /// <summary>Normalises one tenement id the way the rules would ("e28/589" → "E 28/589").</summary>
    public string? NormaliseTenement(string raw) => FindTenements(raw).FirstOrDefault();

    static List<string> Counted(IEnumerable<(string canon, Regex rx)> rules, string text, int min, int max = 12)
    {
        var counts = new List<(string, int)>();
        foreach (var (canon, rx) in rules)
        {
            int n = rx.Matches(text).Count;
            if (n >= min) counts.Add((canon, n));
        }
        return counts.OrderByDescending(c => c.Item2).Take(max).Select(c => c.Item1).ToList();
    }

    public Derived Apply(ItemRow item, IReadOnlyList<PageText> pages)
    {
        var d = new Derived();
        var dataset = item.Dataset;
        d.DatasetLabel = DatasetLabel(dataset);
        d.State = item.M("state") ?? (File.Datasets.TryGetValue(dataset, out var di) ? di.State : null);

        // Sample of the text the rules read: the first 40 pages, up to 400k characters.
        var sb = new StringBuilder();
        foreach (var p in pages) { if (p.N > 40 || sb.Length > 400_000) break; sb.Append(p.Text).Append('\n'); }
        var text = sb.ToString();

        // Titles: the report's title wins for library files, whose file names are codes.
        var collTitle = item.M("collection_title");
        var fileTitle = GoodTitle(item.Title) ? item.Title : null;
        if (collTitle != null)
        {
            d.DisplayTitle = Smarten(collTitle);
            d.Subtitle = fileTitle != null && !fileTitle.Equals(collTitle, StringComparison.OrdinalIgnoreCase) ? fileTitle : item.Name;
        }
        else
        {
            d.DisplayTitle = fileTitle ?? FirstWords(pages) ?? item.Name;
            d.Subtitle = item.Name;
        }

        // Commodities: source metadata first, text mentions when there is none.
        foreach (var c in item.Meta.GetValueOrDefault("commodity") ?? new())
            if (CanonCommodity(c) is { } cc && !d.Commodities.Contains(cc)) d.Commodities.Add(cc);
        if (d.Commodities.Count == 0 && text.Length > 0) d.Commodities = Counted(_commodityText, text, File.CommodityTextMinMentions, 5);

        d.ReportType = ReportType(item.M("report_type"), collTitle ?? fileTitle);

        // Tenements: always from titles and metadata, from the text when mentioned more than once.
        var ten = new List<string>();
        foreach (var src in new[] { collTitle, fileTitle, item.Name })
            if (src != null) foreach (var t in FindTenements(src)) if (!ten.Contains(t)) ten.Add(t);
        foreach (var t in item.Meta.GetValueOrDefault("tenement") ?? new()) if ((NormaliseTenement(t) ?? t.ToUpperInvariant()) is var nt && !ten.Contains(nt)) ten.Add(nt);
        if (text.Length > 0)
            foreach (var g in FindTenements(text).GroupBy(x => x).Where(g => g.Count() >= File.TenementTextMinMentions).OrderByDescending(g => g.Count()).Take(30))
                if (!ten.Contains(g.Key)) ten.Add(g.Key);
        d.Tenements = ten;

        if (text.Length > 0)
        {
            d.Lithology = Counted(_lith, text, File.LithologyTextMinMentions);
            d.Methods = Counted(_methods, text, File.MethodTextMinMentions);
        }

        foreach (var c in item.Meta.GetValueOrDefault("company") ?? new()) d.Companies.Add(CleanCompany(c));
        if (item.M("year") is { } ys && int.TryParse(ys, out var y) && y is > 1800 and < 2200) d.Year = y;
        else if (item.Created is { } cr) d.Year = DateTimeOffset.FromUnixTimeMilliseconds(cr).Year;
        else if (item.Modified is { } mo) d.Year = DateTimeOffset.FromUnixTimeMilliseconds(mo).Year;

        // Location: enricher footprint (set by the indexer) > plugin geo (tables, EXIF) > coordinates in the text.
        if (item.Geo is { Lat: not null } g0) d.Geo = g0;
        else if (text.Length > 0) d.Geo = GeoText.Find(text, d.State);
        if (item.Geo is { Raw: not null } raw && d.Geo != null && d.Geo.Raw == null) d.Geo.Raw = raw.Raw;

        if (item.Collection is { } coll) d.Lookup = _lookups.For(dataset, coll);
        return d;
    }

    public void CheckLookups() => _lookups.Check();

    static bool GoodTitle(string? t) =>
        !string.IsNullOrWhiteSpace(t) && t.Length >= 4 && t.Length < 300 && !t.StartsWith("Microsoft Word", StringComparison.OrdinalIgnoreCase)
        && !Regex.IsMatch(t, @"\.(pdf|docx?|xlsx?|tif|dwg)$", RegexOptions.IgnoreCase) && !t.Equals("untitled", StringComparison.OrdinalIgnoreCase)
        && !Regex.IsMatch(t, @"^[\w\-]+$") && t.Count(char.IsLetter) >= 4;

    static string? FirstWords(IReadOnlyList<PageText> pages)
    {
        var first = pages.FirstOrDefault(p => p.Text.Length >= 20)?.Text;
        if (first == null) return null;
        var sb = new StringBuilder();
        foreach (var w in first.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length + w.Length > 80) { sb.Append('…'); break; }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(w);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>ALL-CAPS report titles to title case, keeping tenement ids and acronyms.</summary>
    public static string Smarten(string t)
    {
        t = t.Trim();
        int letters = t.Count(char.IsLetter);
        if (letters == 0 || t.Count(char.IsUpper) < letters * 0.8) return t;
        var words = t.ToLowerInvariant().Split(' ');
        var keep = new HashSet<string> { "atp", "pl", "epm", "epc", "mdl", "ml", "el", "e", "p", "m", "l", "g", "r", "ppl", "pca", "qgc", "nl", "pty", "ltd", "csg", "cbm", "jv", "rc", "rab", "ip", "em", "aem", "bif", "gsq", "wa", "qld", "nsw", "nt", "sa", "ii", "iii", "iv", "ptl", "tem", "dd", "u3o8", "ree", "pge", "vms" };
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            var core = w.Trim(',', '.', ';', ':', '(', ')');
            if (keep.Contains(core) || core.Any(char.IsDigit)) words[i] = w.ToUpperInvariant();
            else if (i > 0 && core is "and" or "of" or "the" or "for" or "to" or "in" or "on" or "at" or "a" or "by" or "with") words[i] = w;
            else if (w.Length > 0) words[i] = char.ToUpperInvariant(w[0]) + w[1..];
        }
        return string.Join(' ', words);
    }

    static string CleanCompany(string c)
    {
        c = Regex.Replace(c.Trim(), @"\s+", " ");
        if (c.Count(char.IsUpper) > c.Count(char.IsLetter) * 0.8)
            c = Regex.Replace(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(c.ToLowerInvariant()), @"\b(Pty|Ltd|Nl|Plc|Inc|Llc|Jv)\b", m => m.Value switch { "Nl" => "NL", "Jv" => "JV", "Llc" => "LLC", "Plc" => "PLC", var x => x });
        return c;
    }
}

/// <summary>CSV files that add fields to collections, e.g. the ASX company index by (source, item_id).</summary>
public sealed class CsvLookups
{
    readonly SettingsStore _settings;
    readonly Log _log;
    readonly Catalog? _catalog;
    readonly object _lock = new();
    sealed class Table { public DateTime Stamp; public Dictionary<(string, string), Dictionary<string, List<string>>> Rows = new(); }
    readonly Dictionary<string, Table> _tables = new();
    DateTime _lastCheck = DateTime.MinValue;

    public CsvLookups(SettingsStore s, Log log, Catalog? catalog) { _settings = s; _log = log; _catalog = catalog; Check(); }

    string Resolve(string p) => Regex.Replace(p, @"\{root:([^}]+)\}", m => _settings.SourceRoot(m.Groups[1].Value) ?? "?").Replace('/', System.IO.Path.DirectorySeparatorChar);

    /// <summary>Reloads changed files and marks the collections whose values changed for reindexing.</summary>
    public void Check()
    {
        lock (_lock)
        {
            _lastCheck = DateTime.UtcNow;
            foreach (var lk in _settings.Current.Lookups)
            {
                var path = Resolve(lk.Path);
                DateTime stamp;
                try { if (!File.Exists(path)) continue; stamp = File.GetLastWriteTimeUtc(path); } catch { continue; }
                if (_tables.TryGetValue(path, out var t) && t.Stamp == stamp) continue;
                try
                {
                    var nt = new Table { Stamp = stamp, Rows = ReadCsv(path, lk) };
                    if (t != null && _catalog != null)
                    {
                        foreach (var (k, v) in nt.Rows)
                            if (!t.Rows.TryGetValue(k, out var old) || !Same(old, v)) _catalog.MarkCollectionDirty(k.Item1, k.Item2);
                        foreach (var k in t.Rows.Keys.Where(k => !nt.Rows.ContainsKey(k))) _catalog.MarkCollectionDirty(k.Item1, k.Item2);
                    }
                    else if (t == null && _catalog != null && _catalog.Get("lookup:" + path) != stamp.Ticks.ToString())
                    {
                        // First load since a change while the service was off: reindex everything the file names.
                        foreach (var k in nt.Rows.Keys) _catalog.MarkCollectionDirty(k.Item1, k.Item2);
                    }
                    _catalog?.Set("lookup:" + path, stamp.Ticks.ToString());
                    _tables[path] = nt;
                    _log.Info($"Lookup {System.IO.Path.GetFileName(path)}: {nt.Rows.Count:N0} collections");
                }
                catch (IOException) { /* being rewritten: next check */ }
                catch (Exception e) { _log.Warn($"Lookup {path}: {e.Message}"); }
            }
        }
    }

    static bool Same(Dictionary<string, List<string>> a, Dictionary<string, List<string>> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var o) && o.SequenceEqual(kv.Value));

    public Dictionary<string, List<string>> For(string dataset, string collection)
    {
        if (DateTime.UtcNow - _lastCheck > TimeSpan.FromMinutes(2)) Check();
        var res = new Dictionary<string, List<string>>();
        lock (_lock)
            foreach (var t in _tables.Values)
                if (t.Rows.TryGetValue((dataset.ToLowerInvariant(), collection.ToUpperInvariant()), out var f))
                    foreach (var (k, v) in f) foreach (var x in v) res.Add(k, x);
        return res;
    }

    static Dictionary<(string, string), Dictionary<string, List<string>>> ReadCsv(string path, CsvLookup lk)
    {
        var rows = new Dictionary<(string, string), Dictionary<string, List<string>>>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8, true);
        var head = Split(r.ReadLine() ?? "");
        int di = head.FindIndex(h => h.Equals(lk.DatasetColumn, StringComparison.OrdinalIgnoreCase));
        int ci = head.FindIndex(h => h.Equals(lk.CollectionColumn, StringComparison.OrdinalIgnoreCase));
        if (ci < 0) throw new InvalidDataException($"no '{lk.CollectionColumn}' column");
        var cols = lk.Fields.Select(kv => (idx: head.FindIndex(h => h.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)), field: kv.Value)).Where(x => x.idx >= 0).ToList();
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            var c = Split(line);
            if (ci >= c.Count) continue;
            var key = ((di >= 0 && di < c.Count ? c[di] : "").ToLowerInvariant(), c[ci].ToUpperInvariant());
            if (!rows.TryGetValue(key, out var f)) rows[key] = f = new();
            foreach (var (idx, field) in cols) if (idx < c.Count) f.Add(field, c[idx]);
        }
        return rows;
    }

    static List<string> Split(string line)
    {
        var l = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (q) { if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(ch); }
            else if (ch == '"') q = true;
            else if (ch == ',') { l.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(ch);
        }
        l.Add(sb.ToString().Trim());
        return l;
    }
}
