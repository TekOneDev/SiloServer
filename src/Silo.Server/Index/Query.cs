using System.Globalization;
using System.Text.RegularExpressions;
using Silo.Server.Core;

namespace Silo.Server.Index;

/// <summary>A filter the UI shows as a removable chip; <see cref="Token"/> is the query syntax for it.</summary>
public sealed record Chip(string Dim, string Label, string Value, string Token);

public sealed class ParsedQuery
{
    /// <summary>Clauses ANDed together; each is a set of OR alternatives (a word or a "quoted phrase").</summary>
    public List<List<string>> Clauses { get; } = new();
    public List<string> Excluded { get; } = new();
    /// <summary>Facet filters by dimension ("f.commodity" → values). Values within a dimension are ORed.</summary>
    public Dictionary<string, HashSet<string>> Filters { get; } = new();
    public Dictionary<string, HashSet<string>> NotFilters { get; } = new();
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    public double[]? Bbox { get; set; }
    public bool? HasGeo { get; set; }
    public bool? Ocr { get; set; }
    /// <summary>Tenement ids typed as plain words (e.g. "E28/589"), normalised; matched with a strong boost.</summary>
    public List<string> Tenements { get; } = new();
    /// <summary>Report ids typed as plain words (A59033, CR089328).</summary>
    public List<string> ReportIds { get; } = new();
    public List<Chip> Chips { get; } = new();
    public string Text { get; set; } = "";
    public bool LastIsPrefix { get; set; }
    public bool HasText => Clauses.Count > 0;

    public void AddFilter(string dim, string value, bool neg = false)
    {
        var d = neg ? NotFilters : Filters;
        if (!d.TryGetValue(dim, out var set)) d[dim] = set = new(StringComparer.OrdinalIgnoreCase);
        set.Add(value);
    }
}

/// <summary>
/// Google-style queries with mining-aware operators:
///   words  "exact phrase"  -exclude  a OR b
///   commodity:gold  type:annual  tenement:E28/589  company:"Kanowna Lights"  year:1995  year:1990..2005  year:>2010
///   source:wamex  state:wa  rock:komatiite  method:aircore  ext:xlsx  filetype:spreadsheet  report:A59033
///   has:location  ocr:yes  asx:IGO  x.dm_type:"block model"
/// Tenement and report ids typed as plain words are recognised and ranked first.
/// </summary>
public static class QueryParser
{
    static readonly Regex TokenRx = new(@"-?[A-Za-z_.]+:""[^""]*""?|-?""[^""]*""?|\S+", RegexOptions.Compiled);
    static readonly Regex ReportId = new(@"^(A\d{3,6}|CR\d{3,6}|RPT\d{3,7})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static readonly Dictionary<string, (string dim, string label)> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["commodity"] = (F.DCommodity, "Commodity"), ["c"] = (F.DCommodity, "Commodity"),
        ["type"] = (F.DRType, "Report type"), ["reporttype"] = (F.DRType, "Report type"),
        ["tenement"] = (F.DTenement, "Tenement"), ["ten"] = (F.DTenement, "Tenement"),
        ["company"] = (F.DCompany, "Company"), ["operator"] = (F.DCompany, "Company"),
        ["source"] = (F.DDataset, "Source"), ["dataset"] = (F.DDataset, "Source"),
        ["state"] = (F.DState, "State"),
        ["rock"] = (F.DLith, "Rock type"), ["lith"] = (F.DLith, "Rock type"), ["lithology"] = (F.DLith, "Rock type"),
        ["method"] = (F.DMethod, "Method"),
        ["filetype"] = (F.DFType, "File type"),
        ["asx"] = (F.DAsx, "ASX code"),
        ["owner"] = (F.DOwner, "Company now"),
    };

    public static ParsedQuery Parse(string? raw, Rules? rules = null)
    {
        var q = new ParsedQuery();
        raw = (raw ?? "").Trim();
        q.LastIsPrefix = raw.Length > 0 && !raw.EndsWith(' ') && !raw.EndsWith('"');
        var words = new List<(string text, bool quoted, bool neg)>();
        foreach (Match m in TokenRx.Matches(raw))
        {
            var t = m.Value;
            bool neg = t.StartsWith('-') && t.Length > 1;
            var body = neg ? t[1..] : t;
            int colon = body.IndexOf(':');
            if (colon > 0 && !body.StartsWith('"') && Field(q, body[..colon], Unquote(body[(colon + 1)..]), neg, rules)) continue;
            if (body.StartsWith('"')) { var ph = Unquote(body); if (ph.Length > 0) words.Add((ph, true, neg)); continue; }
            words.Add((body, false, neg));
        }

        // Tenement ids can be typed with a space ("E 28/589", "EPM 17533"): look at the words joined.
        if (rules != null)
        {
            var joined = string.Join(' ', words.Where(w => !w.quoted && !w.neg).Select(w => w.text));
            foreach (var t in rules.FindTenements(joined).Distinct()) q.Tenements.Add(t);
        }
        foreach (var w in words.Where(w => !w.quoted && !w.neg && ReportId.IsMatch(w.text))) q.ReportIds.Add(w.text.ToUpperInvariant());

        var parts = new List<string>();
        bool or = false;
        foreach (var (text, quoted, neg) in words)
        {
            if (!quoted && text == "OR") { or = q.Clauses.Count > 0; continue; }
            var term = quoted ? "\"" + text + "\"" : text;
            if (neg) { q.Excluded.Add(term); q.Chips.Add(new Chip("not", "Not", text, "-" + term)); continue; }
            if (or) q.Clauses[^1].Add(term); else q.Clauses.Add([term]);
            or = false;
            parts.Add(term);
        }
        q.Text = string.Join(' ', parts);
        return q;
    }

    static string Unquote(string s) => s.Trim('"');

    static bool Field(ParsedQuery q, string key, string value, bool neg, Rules? rules)
    {
        if (value.Length == 0) return false;
        key = key.ToLowerInvariant();
        if (Operators.TryGetValue(key, out var op))
        {
            var v = value;
            if (op.dim == F.DCommodity && rules?.CanonCommodity(v) is { } c) v = c;
            if (op.dim == F.DTenement && rules?.NormaliseTenement(v) is { } t) v = t;
            if (op.dim == F.DRType && rules != null) v = rules.File.ReportTypes.FirstOrDefault(r => r.Label.Contains(value, StringComparison.OrdinalIgnoreCase))?.Label ?? rules.ReportType(value, null) ?? value;
            if (op.dim == F.DDataset && rules != null && rules.File.Datasets.TryGetValue(v.ToLowerInvariant(), out var ds)) v = ds.Label;
            if (op.dim == F.DLith && rules != null) v = rules.File.Lithology.Where(kv => kv.Key.Contains(value, StringComparison.OrdinalIgnoreCase) || kv.Value.Contains(value.ToLowerInvariant())).Select(kv => kv.Key).FirstOrDefault() ?? value;
            if (op.dim == F.DMethod && rules != null) v = rules.File.Methods.Where(kv => kv.Key.Contains(value, StringComparison.OrdinalIgnoreCase) || kv.Value.Any(x => x.Contains(value.ToLowerInvariant()))).Select(kv => kv.Key).FirstOrDefault() ?? value;
            if (op.dim == F.DState) v = v.ToUpperInvariant();
            if (op.dim == F.DAsx) v = v.ToUpperInvariant();
            q.AddFilter(op.dim, v, neg);
            q.Chips.Add(new Chip(op.dim, (neg ? "Not " : "") + op.label, v, (neg ? "-" : "") + key + ":" + Quote(value)));
            return true;
        }
        if (key.StartsWith("x.") && key.Length > 2)
        {
            q.AddFilter(key, value, neg);
            q.Chips.Add(new Chip(key, key[2..].Replace('_', ' '), value, (neg ? "-" : "") + key + ":" + Quote(value)));
            return true;
        }
        switch (key)
        {
            case "year" or "years":
            {
                var m = Regex.Match(value, @"^(?:(\d{4})\s*(?:\.\.|-|to)\s*(\d{4})|([<>]=?)\s*(\d{4})|(\d{4})s|(\d{4}))$");
                if (!m.Success) return false;
                if (m.Groups[1].Success) { q.YearFrom = int.Parse(m.Groups[1].Value); q.YearTo = int.Parse(m.Groups[2].Value); }
                else if (m.Groups[3].Success)
                {
                    int y = int.Parse(m.Groups[4].Value);
                    if (m.Groups[3].Value.StartsWith('>')) q.YearFrom = m.Groups[3].Value == ">" ? y + 1 : y; else q.YearTo = m.Groups[3].Value == "<" ? y - 1 : y;
                }
                else if (m.Groups[5].Success) { q.YearFrom = int.Parse(m.Groups[5].Value); q.YearTo = q.YearFrom + 9; }
                else { q.YearFrom = q.YearTo = int.Parse(m.Groups[6].Value); }
                q.Chips.Add(new Chip("year", "Year", YearLabel(q.YearFrom, q.YearTo), "year:" + value));
                return true;
            }
            case "before" when int.TryParse(value, out var b): q.YearTo = b - 1; q.Chips.Add(new Chip("year", "Year", "before " + b, "before:" + value)); return true;
            case "after" or "since" when int.TryParse(value, out var a): q.YearFrom = key == "after" ? a + 1 : a; q.Chips.Add(new Chip("year", "Year", (key == "after" ? "after " : "since ") + a, key + ":" + value)); return true;
            case "ext":
                q.AddFilter("ext", value.TrimStart('.').ToLowerInvariant(), neg);
                q.Chips.Add(new Chip("ext", "Extension", value, (neg ? "-" : "") + "ext:" + value)); return true;
            case "report" or "id":
                q.AddFilter("collid", value.ToLowerInvariant(), neg);
                q.Chips.Add(new Chip("collid", "Report", value.ToUpperInvariant(), "report:" + value)); return true;
            case "has" when value is "location" or "geo" or "map" or "coordinates":
                q.HasGeo = !neg; q.Chips.Add(new Chip("has", neg ? "No location" : "Has location", "", (neg ? "-" : "") + "has:location")); return true;
            case "ocr" when value is "yes" or "no" or "true" or "false":
                q.Ocr = value is "yes" or "true"; q.Chips.Add(new Chip("ocr", q.Ocr == true ? "Scanned (OCR)" : "Not scanned", "", "ocr:" + value)); return true;
            case "in" or "bbox" when TryBbox(value, out var bb):
                q.Bbox = bb; q.Chips.Add(new Chip("bbox", "Map area", AreaLabel(bb), "in:" + value)); return true;
        }
        return false;
    }

    public static bool TryBbox(string s, out double[] bb)
    {
        bb = [];
        var p = s.Trim('(', ')', '[', ']').Split(',').Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
        if (p.Length != 4 || p.Any(double.IsNaN) || p[0] >= p[2] || p[1] >= p[3]) return false;
        bb = p; return true;
    }

    public static string AreaLabel(double[] b) => $"{-b[1]:0.0}°S–{-b[3]:0.0}°S, {b[0]:0.0}°E–{b[2]:0.0}°E";
    public static string YearLabel(int? f, int? t) => f == t && f != null ? f.ToString()! : f != null && t != null ? $"{f}–{t}" : f != null ? $"{f} on" : $"to {t}";
    static string Quote(string v) => v.Contains(' ') ? "\"" + v + "\"" : v;
}
