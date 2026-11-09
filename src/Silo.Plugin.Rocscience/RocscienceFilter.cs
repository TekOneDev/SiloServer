using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Silo.Plugins.Rocscience;

/// <summary>
/// Rocscience RS3 files, read in place:
///   .rs3compute  plain-text solver input: version, units, stages, global offset, named materials and strength models
///   .rs3v3       SQLite project: tables and the embedded project XML
///   .rs3results  DVTOKENFILE results: run id, compute time, RS3 version, result sections and value ranges (~1 KB read)
///   engine       solver log in each results folder: stages, convergence, run time
/// Formats were worked out from sample files (see the RS3 write-up); unknown variants degrade to text.
/// </summary>
public sealed class RocscienceFilter : FilterPlugin
{
    public override PluginManifest Manifest { get; } = new()
    {
        Id = "silo.filter.rocscience",
        Name = "Rocscience RS3 models",
        Version = "0.9.0",
        Kind = "filter",
        Description = "RS3 compute, project, results and engine log files: materials, stages, units, solver and convergence.",
        Publisher = "Silo",
        Extensions = [".rs3compute", ".rs3v3", ".rs3results", ".rs3log"],
        Names = ["engine"],
        Concurrency = 6,
        TimeoutSec = 300,
        MaxFileMB = 1_000_000,
        ReadsInPlace = true,
        Fields =
        [
            new FieldSpec { Name = "software", Label = "Software", Type = "keyword", Facet = true, Show = true },
            new FieldSpec { Name = "rs3_file", Label = "RS3 file kind", Type = "keyword", Facet = true, Show = true },
            new FieldSpec { Name = "rs3_material", Label = "Material", Type = "keyword", Facet = true },
            new FieldSpec { Name = "strength_model", Label = "Strength model", Type = "keyword", Facet = true, Show = true },
            new FieldSpec { Name = "analysis", Label = "Analysis", Type = "keyword", Facet = true, Show = true },
            new FieldSpec { Name = "rs3_version", Label = "RS3 version", Type = "keyword", Facet = false, Show = true },
            new FieldSpec { Name = "rs3_stages", Label = "Stages", Type = "number", Facet = false, Show = true },
            new FieldSpec { Name = "rs3_run", Label = "Run id", Type = "keyword", Facet = false },
        ],
    };

    public override ExtractResult Extract(ExtractArgs a, CancellationToken ct)
    {
        var r = new ExtractResult();
        r.Fields.Add("software", "Rocscience RS3");
        var name = Path.GetFileName(a.Path);
        if (a.Ext == ".rs3compute") Compute(a.Path, r);
        else if (a.Ext == ".rs3v3") Project(a.Path, r);
        else if (a.Ext == ".rs3results") Results(a.Path, r);
        else Engine(a.Path, r);
        r.TotalPages = r.Pages.Count;
        return r;
    }

    // ---------- .rs3compute (text) ----------

    static readonly string[] Models = ["Generalized Hoek-Brown", "Hoek-Brown", "Mohr-Coulomb", "Drucker-Prager", "Barton-Bandis", "Cam-Clay", "Elastic", "Jointed Generalized Hoek-Brown", "Softening Hardening"];

    static void Compute(string path, ExtractResult r)
    {
        r.Fields.Add("rs3_file", "Compute (solver input)");
        // The header and material blocks come first; the mesh that follows is numbers only. Read up to 8 MB.
        var text = ReadHead(path, 8 * 1024 * 1024);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var keep = new StringBuilder();
        var materials = new List<string>();
        int stages = 0;
        string? section = null;
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Length == 0) continue;
            if (l.StartsWith('#')) { section = l.TrimStart('#').Trim(); keep.AppendLine(l); continue; }
            // Skip pure number rows (mesh, coordinates).
            if (Regex.IsMatch(l, @"^[-+\d\s.,eE]+$")) continue;
            if (keep.Length < 2_000_000) keep.AppendLine(l);
            var kv = Regex.Match(l, @"^([A-Za-z_][\w \-/()]*?)\s*[=:]\s*(.+)$");
            if (!kv.Success) continue;
            var key = kv.Groups[1].Value.Trim().ToLowerInvariant().Replace(' ', '_');
            var val = kv.Groups[2].Value.Trim().Trim('"');
            switch (key)
            {
                case "version" or "program_version" or "rs3_version": r.Fields.Add("rs3_version", val); break;
                case "units" or "unit_system" or "stress_units": r.Fields.Add("units", val); break;
                case "number_of_stages" or "num_stages" or "stages" when int.TryParse(val, out var s): stages = Math.Max(stages, s); break;
                case "global_offset" or "globaloffset":
                    var nums = Regex.Matches(val, @"-?\d+(?:\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
                    if (nums.Count >= 2) r.Geo = new Geo { Raw = $"model offset {nums[0]:F0}E {nums[1]:F0}N", Basis = "model-offset" };
                    break;
                case "name" or "material_name" or "materialname" when section != null && section.Contains("MODEL", StringComparison.OrdinalIgnoreCase):
                    if (val.Length is > 0 and < 80 && !materials.Contains(val)) materials.Add(val);
                    break;
                case "ssr" or "ssr_analysis" or "strength_reduction" or "srf_analysis" when val is "1" or "true" or "on" or "yes":
                    r.Fields.Add("analysis", "Shear strength reduction (SSR)"); break;
            }
            if (l.StartsWith("STAGE", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(l, @"^STAGE\b", RegexOptions.IgnoreCase)) stages++;
        }
        foreach (var m in Models)
            if (text.Contains(m, StringComparison.OrdinalIgnoreCase) || text.Contains(m.Replace("-", "_"), StringComparison.OrdinalIgnoreCase) || text.Contains(m.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
            { r.Fields.Add("strength_model", m == "Hoek-Brown" && r.Fields.ContainsKey("strength_model") ? null : m); }
        if (Regex.IsMatch(text, @"\b(SSR|SRF)\b") && !r.Fields.ContainsKey("analysis")) r.Fields.Add("analysis", "Shear strength reduction (SSR)");
        if (Regex.IsMatch(text, @"groundwater|pore.?pressure|permeab", RegexOptions.IgnoreCase)) r.Fields.Add("analysis", "Groundwater");
        foreach (var m in materials) { r.Fields.Add("rs3_material", m); r.Entities.Add(new Entity("material", m)); }
        if (stages > 0) r.Fields.Add("rs3_stages", stages.ToString());
        r.Pages.Add(new PageText { N = 1, Text = keep.ToString() });
        r.Summary = $"RS3 solver input" + (stages > 0 ? $" · {stages} stages" : "") + (materials.Count > 0 ? $" · {materials.Count} materials: {string.Join(", ", materials.Take(6))}{(materials.Count > 6 ? "…" : "")}" : "");
    }

    // ---------- .rs3v3 (SQLite) ----------

    static void Project(string path, ExtractResult r)
    {
        r.Fields.Add("rs3_file", "Project (model)");
        var sb = new StringBuilder();
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        con.Open();
        ReadProject(con, r, sb);
        r.Pages.Add(new PageText { N = 1, Text = sb.ToString() });
    }

    static void ReadProject(SqliteConnection con, ExtractResult r, StringBuilder sb)
    {
        var tables = new List<string>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) tables.Add(rd.GetString(0));
        }
        sb.AppendLine("RS3 project tables: " + string.Join(", ", tables));
        if (tables.Contains("__files__"))
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT * FROM __files__ LIMIT 50";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                for (int i = 0; i < rd.FieldCount; i++)
                {
                    if (rd.IsDBNull(i)) continue;
                    string? s = rd.GetFieldType(i) == typeof(byte[]) ? Decode((byte[])rd.GetValue(i)) : rd.GetValue(i)?.ToString();
                    if (s == null) continue;
                    if (s.TrimStart().StartsWith('<')) XmlText(s, r, sb);
                    else if (s.Length < 300) sb.AppendLine(rd.GetName(i) + ": " + s);
                }
        }
        if (tables.Contains("versions"))
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT * FROM versions LIMIT 5";
            try
            {
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) for (int i = 0; i < rd.FieldCount; i++) if (!rd.IsDBNull(i)) { var v = rd.GetValue(i)?.ToString(); sb.AppendLine("version " + rd.GetName(i) + ": " + v); if (i == 0 && v is { Length: < 20 }) r.Fields.Add("rs3_version", v); }
            }
            catch { }
        }
        r.Summary = $"RS3 project · {tables.Count} tables";
        if (r.Fields.TryGetValue("rs3_material", out var mats)) r.Summary += $" · {mats.Count} materials: {string.Join(", ", mats.Take(6))}";
    }

    static string? Decode(byte[] b)
    {
        if (b.Length > 50_000_000) return null;
        if (b.Length > 2 && b[0] == 0x1f && b[1] == 0x8b)
        {
            using var gz = new System.IO.Compression.GZipStream(new MemoryStream(b), System.IO.Compression.CompressionMode.Decompress);
            using var ms = new MemoryStream(); gz.CopyTo(ms); b = ms.ToArray();
        }
        int printable = b.Take(4096).Count(x => x is >= 9 and < 127);
        return printable > Math.Min(4096, b.Length) * 0.9 ? Encoding.UTF8.GetString(b) : null;
    }

    static void XmlText(string xml, ExtractResult r, StringBuilder sb)
    {
        // Element and attribute values that read as words; material names from name="" on material-like elements.
        foreach (Match m in Regex.Matches(xml, @"<(\w*[Mm]aterial\w*)[^>]*\bname=""([^""]{1,80})""")) { r.Fields.Add("rs3_material", m.Groups[2].Value); r.Entities.Add(new Entity("material", m.Groups[2].Value)); }
        foreach (Match m in Regex.Matches(xml, @"\b(?:name|title|label|description)=""([^""]{2,200})""")) sb.AppendLine(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(xml, @">([^<>]{3,400})<")) { var v = m.Groups[1].Value.Trim(); if (v.Any(char.IsLetter)) sb.AppendLine(v); }
        if (sb.Length > 2_000_000) sb.Length = 2_000_000;
    }

    // ---------- .rs3results (DVTOKENFILE) ----------

    public sealed record DvToken(List<(string name, long offset, long length)> Sections, double? Format, DateTime? Computed, ulong? RunId, double? Version, List<(double min, double max)> Ranges);

    public static DvToken? ReadDvToken(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        var head = new byte[19];
        if (fs.Read(head, 0, 19) < 19 || Encoding.ASCII.GetString(head, 0, 11) != "DVTOKENFILE") return null;
        long toc = BitConverter.ToInt64(head, 11);
        if (toc <= 19 || toc >= fs.Length) return null;
        fs.Seek(toc, SeekOrigin.Begin);
        var tail = new byte[Math.Min(1 << 20, fs.Length - toc)];
        fs.ReadExactly(tail);
        int p = 0;
        uint n = BitConverter.ToUInt32(tail, p); p += 4;
        var secs = new List<(string, long, long)>();
        var offs = new List<(string name, long off)>();
        for (int i = 0; i < n && p + 12 <= tail.Length; i++)
        {
            long off = BitConverter.ToInt64(tail, p); p += 8;
            int len = (int)BitConverter.ToUInt32(tail, p); p += 4;
            if (len > 256 || p + len > tail.Length) break;
            offs.Add((Encoding.ASCII.GetString(tail, p, len), off)); p += len;
        }
        for (int i = 0; i < offs.Count; i++) secs.Add((offs[i].name, offs[i].off, (i + 1 < offs.Count ? offs[i + 1].off : toc) - offs[i].off));
        double? format = null, version = null; DateTime? computed = null; ulong? run = null;
        var ranges = new List<(double, double)>();
        foreach (var (name, off, len) in secs)
        {
            if (name == "HEADER" && len is >= 32 and < 4096)
            {
                var h = new byte[len]; fs.Seek(off, SeekOrigin.Begin); fs.ReadExactly(h);
                format = BitConverter.ToDouble(h, 0);
                var t = BitConverter.ToInt64(h, 8);
                if (t > 946684800 && t < 4102444800) computed = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime;
                run = BitConverter.ToUInt64(h, 16);
                version = Math.Round(BitConverter.ToDouble(h, (int)len - 8), 3);
            }
            if (name == "DATA_RANGES" && len is >= 8 and < 65536)
            {
                var b = new byte[len]; fs.Seek(off, SeekOrigin.Begin); fs.ReadExactly(b);
                long k = BitConverter.ToInt64(b, 0);
                for (int i = 0; i < k && 24 + 16 * i <= len; i++) ranges.Add((BitConverter.ToDouble(b, 8 + 16 * i), BitConverter.ToDouble(b, 16 + 16 * i)));
            }
        }
        return new DvToken(secs, format, computed, run, version, ranges);
    }

    static void Results(string path, ExtractResult r)
    {
        r.Fields.Add("rs3_file", "Results");
        var name = Path.GetFileNameWithoutExtension(path);
        var kind = Regex.Match(name, @"^(r-)?([a-z]+)-?(\d+|all)?", RegexOptions.IgnoreCase);
        string what = kind.Groups[2].Value.ToLowerInvariant() switch { "gw" => "groundwater", "solids" => "solids (stress, strain, displacement)", "misc" => "mesh and materials", var x => x };
        bool recovery = kind.Groups[1].Success;
        var stage = kind.Groups[3].Value;
        if (recovery) { r.Summary = $"RS3 recovery file ({what}, stage {stage})"; r.Pages.Add(new PageText { N = 1, Text = r.Summary }); return; }
        var dv = ReadDvToken(path);
        var sb = new StringBuilder();
        sb.AppendLine($"RS3 results: {what}" + (stage.Length > 0 ? (stage == "all" ? ", whole run" : $", stage {stage}") : ""));
        if (dv == null) { sb.AppendLine("Not a DVTOKENFILE container."); r.Pages.Add(new PageText { N = 1, Text = sb.ToString() }); r.Summary = sb.ToString().Trim(); return; }
        if (dv.Version is { } v && v is > 1 and < 100) { r.Fields.Add("rs3_version", v.ToString("0.000", CultureInfo.InvariantCulture)); sb.AppendLine($"RS3 version {v:0.000}"); }
        if (dv.Computed is { } c) { r.Created = c; sb.AppendLine($"Computed {c:yyyy-MM-dd HH:mm} UTC"); }
        if (dv.RunId is { } run) { r.Fields.Add("rs3_run", run.ToString("x")); sb.AppendLine($"Run id {run:x}"); }
        sb.AppendLine("Sections: " + string.Join(", ", dv.Sections.Select(s => $"{s.name.Replace('_', ' ').ToLowerInvariant()} ({s.length / 1048576.0:0.#} MB)")));
        foreach (var s in dv.Sections) r.Entities.Add(new Entity("result", s.name.Replace('_', ' ').ToLowerInvariant()));
        if (dv.Ranges.Count > 0) sb.AppendLine("Value ranges: " + string.Join("; ", dv.Ranges.Take(40).Select(x => $"{x.min:G4} to {x.max:G4}")));
        r.Pages.Add(new PageText { N = 1, Text = sb.ToString() });
        r.Summary = $"RS3 results · {what}" + (stage.Length > 0 ? (stage == "all" ? " · whole run" : $" · stage {stage}") : "") + (dv.Computed is { } cc ? $" · computed {cc:d MMM yyyy}" : "");
    }

    // ---------- engine log ----------

    static void Engine(string path, ExtractResult r)
    {
        r.Fields.Add("rs3_file", "Engine log");
        var text = ReadHead(path, 20 * 1024 * 1024);
        var stages = Regex.Matches(text, @"\bstage\s*(\d+)", RegexOptions.IgnoreCase).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
        var warnings = Regex.Matches(text, @"(not\s+converge|did\s+not\s+converge|non-?converg)", RegexOptions.IgnoreCase).Count;
        if (stages > 0) r.Fields.Add("rs3_stages", stages.ToString());
        r.Fields.Add("analysis", Regex.IsMatch(text, @"\b(SSR|SRF|strength reduction)\b", RegexOptions.IgnoreCase) ? "Shear strength reduction (SSR)" : null);
        r.Summary = $"RS3 solver log · {stages} stages" + (warnings > 0 ? $" · {warnings} convergence warnings" : " · converged");
        int n = 0;
        for (int pos = 0; pos < text.Length; pos += 50_000) r.Pages.Add(new PageText { N = ++n, Text = text.Substring(pos, Math.Min(50_000, text.Length - pos)) });
    }

    static string ReadHead(string path, int max)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var buf = new byte[(int)Math.Min(max, fs.Length)];
        int got = 0, k;
        while (got < buf.Length && (k = fs.Read(buf, got, buf.Length - got)) > 0) got += k;
        return Encoding.UTF8.GetString(buf, 0, got);
    }
}
