using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using HtmlAgilityPack;

namespace Silo.Plugins.Office;

/// <summary>
/// Word, Excel, PowerPoint, HTML, RTF and plain-text family (txt, csv, las, log, xml, json...).
/// Spreadsheets and CSV files are also sniffed for mining data tables (collars, assays, lithology logs,
/// downhole surveys) so they can be filtered by data type, and lat/long columns give a location.
/// </summary>
public sealed class OfficeFilter : FilterPlugin
{
    public override PluginManifest Manifest { get; } = new()
    {
        Id = "silo.filter.office",
        Name = "Office documents and text",
        Version = "1.0.0",
        Kind = "filter",
        Description = "Word, Excel, PowerPoint, HTML, RTF, CSV and text files; spots drillhole data tables.",
        Publisher = "Silo",
        Extensions = [".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".pptx", ".pptm", ".htm", ".html", ".rtf",
                      ".txt", ".csv", ".tsv", ".las", ".log", ".xml", ".json", ".md", ".dat", ".asc", ".prn", ".kml"],
        Concurrency = 8,
        TimeoutSec = 600,
        MaxFileMB = 400,
        Fields =
        [
            new FieldSpec { Name = "data_type", Label = "Data type", Type = "keyword", Facet = true, Show = true },
            new FieldSpec { Name = "columns", Label = "Columns", Type = "text", Facet = false },
        ],
    };

    const int PageChars = 6000;

    public override ExtractResult Extract(ExtractArgs a, CancellationToken ct)
    {
        var r = new ExtractResult();
        switch (a.Ext)
        {
            case ".docx" or ".docm" or ".dotx": Docx(a.Path, r); break;
            case ".xlsx" or ".xlsm": Xlsx(a.Path, r, a.MaxChars); break;
            case ".pptx" or ".pptm": Pptx(a.Path, r); break;
            case ".htm" or ".html": Html(a.Path, r, a.MaxChars); break;
            case ".rtf": Paged(r, Rtf(ReadText(a.Path, a.MaxChars))); break;
            case ".csv" or ".tsv": Csv(a.Path, r, a.MaxChars); break;
            default: Paged(r, ReadText(a.Path, a.MaxChars)); break;
        }
        r.TotalPages = r.Pages.Count;
        return r;
    }

    /// <summary>Splits long flowing text into pseudo-pages at paragraph breaks, so snippets point somewhere useful.</summary>
    static void Paged(ExtractResult r, string text)
    {
        int n = r.Pages.Count;
        int pos = 0;
        while (pos < text.Length)
        {
            int len = Math.Min(PageChars, text.Length - pos);
            if (pos + len < text.Length)
            {
                int cut = text.LastIndexOf('\n', pos + len, Math.Min(len, 1500));
                if (cut > pos) len = cut - pos + 1;
            }
            var chunk = Regex.Replace(text.Substring(pos, len), @"[ \t]+", " ").Trim();
            if (chunk.Length > 0) r.Pages.Add(new PageText { N = ++n, Text = chunk });
            pos += len;
        }
    }

    static void Docx(string path, ExtractResult r)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var sb = new StringBuilder();
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body != null)
            foreach (var el in body.ChildElements)
            {
                if (el is DocumentFormat.OpenXml.Wordprocessing.Table t)
                    foreach (var row in t.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>())
                        sb.AppendLine(string.Join(" | ", row.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>().Select(c => c.InnerText)));
                else sb.AppendLine(el.InnerText);
            }
        Paged(r, sb.ToString());
        var props = doc.PackageProperties;
        r.Title = Clean(props.Title); r.Author = Clean(props.Creator); r.Created = props.Created;
    }

    static void Pptx(string path, ExtractResult r)
    {
        using var doc = PresentationDocument.Open(path, false);
        var pres = doc.PresentationPart;
        int n = 0;
        if (pres?.Presentation?.SlideIdList != null)
            foreach (var id in pres.Presentation.SlideIdList.Elements<DocumentFormat.OpenXml.Presentation.SlideId>())
            {
                n++;
                if (id.RelationshipId?.Value is not string rid || pres.GetPartById(rid) is not SlidePart sp) continue;
                var sb = new StringBuilder();
                foreach (var t in sp.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>()) sb.AppendLine(t.InnerText);
                if (sp.NotesSlidePart?.NotesSlide != null)
                    foreach (var t in sp.NotesSlidePart.NotesSlide.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>()) sb.AppendLine(t.InnerText);
                r.Pages.Add(new PageText { N = n, Text = Regex.Replace(sb.ToString(), @"\s+", " ").Trim() });
            }
        var props = doc.PackageProperties;
        r.Title = Clean(props.Title); r.Author = Clean(props.Creator); r.Created = props.Created;
    }

    static void Xlsx(string path, ExtractResult r, long maxChars)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart;
        long total = 0;
        if (wb != null)
        {
            var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];
            var sheets = wb.Workbook?.Sheets?.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().ToList() ?? new();
            int n = 0;
            foreach (var sheet in sheets)
            {
                n++;
                if (sheet.Id?.Value is not string rid || wb.GetPartById(rid) is not WorksheetPart wsp) continue;
                var rows = new List<List<string>>();
                var row = new List<string>();
                var sb = new StringBuilder();
                sb.Append("Sheet ").Append(sheet.Name?.Value).AppendLine(":");
                using var reader = DocumentFormat.OpenXml.OpenXmlReader.Create(wsp);
                while (reader.Read())
                {
                    if (reader.ElementType == typeof(DocumentFormat.OpenXml.Spreadsheet.Row) && reader.IsEndElement)
                    {
                        if (row.Count > 0) { if (rows.Count < 5000) rows.Add(row); sb.AppendLine(string.Join("\t", row)); }
                        row = new List<string>();
                        if (sb.Length + total > maxChars) break;
                        continue;
                    }
                    if (reader.ElementType != typeof(DocumentFormat.OpenXml.Spreadsheet.Cell) || reader.IsEndElement) continue;
                    var cell = (DocumentFormat.OpenXml.Spreadsheet.Cell)reader.LoadCurrentElement()!;
                    var v = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? "";
                    if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString && int.TryParse(v, out var idx) && idx < shared.Length) v = shared[idx];
                    row.Add(v);
                }
                total += sb.Length;
                // Big data sheets: keep the header and a sample in the text, the rest only counts.
                var text = sb.ToString();
                r.Pages.Add(new PageText { N = n, Text = text.Length > 400_000 ? text[..400_000] : text });
                Tabular.Sniff(rows, r);
                if (total > maxChars) break;
            }
        }
        var props = doc.PackageProperties;
        r.Title = Clean(props.Title); r.Author = Clean(props.Creator); r.Created = props.Created;
    }

    static void Csv(string path, ExtractResult r, long maxChars)
    {
        var text = ReadText(path, Math.Min(maxChars, 20_000_000));
        var lines = text.Split('\n');
        char sep = lines.Length > 0 && lines[0].Count(c => c == '\t') > lines[0].Count(c => c == ',') ? '\t' : ',';
        var rows = lines.Take(5000).Select(l => SplitCsv(l.TrimEnd('\r'), sep)).Where(x => x.Count > 0).ToList();
        Tabular.Sniff(rows, r);
        Paged(r, text.Length > 2_000_000 ? text[..2_000_000] : text);
    }

    static List<string> SplitCsv(string line, char sep)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        foreach (var ch in line)
        {
            if (ch == '"') { q = !q; continue; }
            if (ch == sep && !q) { list.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        if (sb.Length > 0 || list.Count > 0) list.Add(sb.ToString().Trim());
        return list;
    }

    static void Html(string path, ExtractResult r, long maxChars)
    {
        var html = ReadText(path, maxChars);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        r.Title = Clean(doc.DocumentNode.SelectSingleNode("//title")?.InnerText);
        foreach (var n in doc.DocumentNode.SelectNodes("//script|//style") ?? Enumerable.Empty<HtmlNode>()) n.Remove();
        Paged(r, HtmlEntity.DeEntitize(doc.DocumentNode.InnerText));
    }

    static string Rtf(string raw)
    {
        var text = Regex.Replace(raw, @"\\par[d]?", "\n");
        text = Regex.Replace(text, @"\{\\\*[^{}]*\}|\\'[0-9a-f]{2}|\\[a-z]+-?\d* ?|[{}]", " ", RegexOptions.IgnoreCase);
        return Regex.Replace(text, @"[ \t]+", " ").Trim();
    }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Reads text with BOM detection; UTF-8, falling back to Latin-1. Returns "" for binary files.</summary>
    public static string ReadText(string path, long maxChars)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var buf = new byte[(int)Math.Min(Math.Min(maxChars, int.MaxValue / 2), s.Length)];
        int read = 0, n;
        while (read < buf.Length && (n = s.Read(buf, read, buf.Length - read)) > 0) read += n;
        if (read == 0) return "";
        int nul = 0; for (int i = 0; i < Math.Min(read, 4096); i++) if (buf[i] == 0) nul++;
        bool utf16 = read >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF));
        if (!utf16 && nul > Math.Min(read, 4096) / 20) return "";
        var enc = utf16 ? (buf[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode) : new UTF8Encoding(false, true);
        try { return enc.GetString(buf, 0, read).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(buf, 0, read); }
    }
}

/// <summary>Recognises common mining data tables from their header row.</summary>
public static class Tabular
{
    static readonly string[] HoleCols = ["holeid", "hole_id", "hole", "bhid", "hole_no", "holeno", "drillhole", "site_id", "siteid", "hole_name"];
    static readonly string[] EastCols = ["east", "easting", "mga_east", "mgae", "x", "amg_east", "e_mga", "mga_e", "grid_east"];
    static readonly string[] NorthCols = ["north", "northing", "mga_north", "mgan", "y", "amg_north", "n_mga", "mga_n", "grid_north"];
    static readonly string[] LatCols = ["lat", "latitude", "lat_gda94", "lat_wgs84", "dec_lat", "gda94_lat"];
    static readonly string[] LonCols = ["lon", "long", "longitude", "lon_gda94", "long_gda94", "lon_wgs84", "dec_long", "gda94_long"];
    static readonly string[] FromCols = ["from", "depth_from", "from_m", "mfrom", "fromm", "depthfrom"];
    static readonly string[] ToCols = ["to", "depth_to", "to_m", "mto", "tom", "depthto"];
    static readonly string[] Elements = ["au", "ag", "cu", "pb", "zn", "ni", "co", "fe", "mn", "as", "mo", "u", "u3o8", "li", "li2o", "sn", "w", "pt", "pd", "cr", "v", "ti", "sio2", "al2o3", "fe2o3", "mgo", "cao", "s"];

    static string Norm(string h) => Regex.Replace(h.Trim().ToLowerInvariant(), @"[\s\-\.\(\)\[\]/]+", "_").Trim('_');
    static string Base(string h) => Regex.Replace(Norm(h), @"_?(ppm|ppb|pct|percent|gt|g_t|wt|ppm_|_ppm)$", "").Trim('_');

    public static void Sniff(List<List<string>> rows, ExtractResult r)
    {
        // The header is the first of the top rows with mostly non-numeric cells.
        int hi = -1;
        for (int i = 0; i < Math.Min(rows.Count, 15); i++)
        {
            var row = rows[i];
            if (row.Count < 3) continue;
            int words = row.Count(c => c.Length > 0 && !double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            if (words >= row.Count * 0.7) { hi = i; break; }
        }
        if (hi < 0) return;
        var head = rows[hi].Select(Norm).ToList();
        var bases = rows[hi].Select(Base).ToList();
        int Col(string[] names) => head.FindIndex(h => names.Contains(h));
        bool Has(string[] names) => Col(names) >= 0;
        r.Fields.Add("columns", string.Join(" ", rows[hi].Where(c => c.Length > 0).Take(80)));

        bool hole = Has(HoleCols), from = Has(FromCols) && Has(ToCols);
        bool ne = Has(EastCols) && Has(NorthCols), ll = Has(LatCols) && Has(LonCols);
        int elements = bases.Count(b => Elements.Contains(b));
        bool lith = head.Any(h => h.StartsWith("lith") || h is "rock_type" or "rocktype" or "geology" or "regolith");
        bool survey = head.Any(h => h.StartsWith("azi")) && head.Any(h => h.StartsWith("dip") || h.StartsWith("incl"));

        if (hole && (ne || ll) && !from) r.Fields.Add("data_type", "Drillhole collars");
        if (from && elements >= 1) r.Fields.Add("data_type", "Assays / geochemistry");
        else if (!from && elements >= 3 && (ne || ll)) r.Fields.Add("data_type", "Surface geochemistry");
        if (from && lith) r.Fields.Add("data_type", "Lithology logs");
        if (survey && head.Any(h => h.Contains("depth"))) r.Fields.Add("data_type", "Downhole surveys");
        if (head.Any(h => h is "ucs" or "ucs_mpa" or "rqd" or "rmr" or "q_value" or "gsi")) r.Fields.Add("data_type", "Geotechnical data");
        if (hole) r.Entities.AddRange(rows.Skip(hi + 1).Select(x => Col(HoleCols) < x.Count ? x[Col(HoleCols)] : "").Where(v => v.Length is > 1 and < 40).Distinct().Take(500).Select(v => new Entity("hole", v)));

        if (ll)
        {
            int la = Col(LatCols), lo = Col(LonCols);
            double w = 999, s = 999, e = -999, n = -999; int k = 0;
            foreach (var row in rows.Skip(hi + 1))
            {
                if (la >= row.Count || lo >= row.Count) continue;
                if (!double.TryParse(row[la], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                    !double.TryParse(row[lo], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
                if (Math.Abs(y) > 90 || Math.Abs(x) > 180 || (x == 0 && y == 0)) continue;
                w = Math.Min(w, x); e = Math.Max(e, x); s = Math.Min(s, y); n = Math.Max(n, y); k++;
            }
            if (k > 0) r.Geo = new Geo { Lat = (s + n) / 2, Lon = (w + e) / 2, Bbox = [w, s, e, n], Basis = "table" };
        }
    }
}
