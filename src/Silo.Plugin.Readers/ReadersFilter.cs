using System.Text.RegularExpressions;
using Silo.Core;
using Silo.Core.Mining;

namespace Silo.Plugins.Readers;

/// <summary>
/// The file types Silo Desktop reads and Silo Server did not (0.1.6), through the shared readers in SiloCommon:
/// older Office files, OpenDocument, saved mail, zip listings, CAD drawings (DWG, DXF, DGN, STEP), 3D print and mesh
/// files, and mining models (Datamine projects and .dmx, Leapfrog, Rocscience Slide/RS2, OMF, Surfer grids).
/// Datamine .dm and Rocscience RS3 stay with their own plugins for now.
/// </summary>
public sealed class ReadersFilter : FilterPlugin
{
    static readonly string[] Documents = [".doc", ".dot", ".xls", ".xlsb", ".ppt", ".pps", ".eml", ".msg", ".zip", ".odt", ".ods", ".odp"];
    /// <summary>Model types read here: everything the shared model reader knows except the two the older plugins own.</summary>
    static readonly string[] Models = ModelReader.Extensions.Where(e => e is not ("dm" or "rs3v3")).Select(e => "." + e).ToArray();

    public override PluginManifest Manifest { get; } = new()
    {
        Id = "silo.filter.readers",
        Name = "Older Office, mail, CAD and model files",
        Version = "1.0.0",
        Kind = "filter",
        Description = "Word/Excel/PowerPoint 97-2003, OpenDocument, .eml/.msg, zip listings, DWG/DXF/DGN/STEP, 3D print and mesh files, Datamine projects, Leapfrog, Slide/RS2, OMF and Surfer grids. Shared with Silo Desktop.",
        Publisher = "Silo",
        Extensions = [.. Documents, .. Models],
        Concurrency = 6,
        TimeoutSec = 600,
        MaxFileMB = 4096,
        ReadsInPlace = true,
        Fields =
        [
            new FieldSpec { Name = "software", Label = "Software", Type = "keyword", Facet = true, Show = true },
            new FieldSpec { Name = "model_kind", Label = "Model type", Type = "keyword", Facet = true, Show = true },
            new FieldSpec { Name = "fs_min", Label = "Lowest factor of safety", Type = "number", Facet = false, Show = true },
            new FieldSpec { Name = "has_results", Label = "Has results", Type = "keyword", Facet = true },
            new FieldSpec { Name = "units", Label = "Units", Type = "keyword", Facet = false },
        ],
    };

    const int PageChars = 6000;

    public override ExtractResult Extract(ExtractArgs a, CancellationToken ct)
    {
        var ext = (a.Ext.StartsWith('.') ? a.Ext[1..] : a.Ext).ToLowerInvariant();
        var got = Extractor.ExtractFileAsync(a.Path, ext, null, ct).GetAwaiter().GetResult();
        return Convert(got, a.Path);
    }

    /// <summary>The shared reader's result in the plugin contract's shape (public for tests).</summary>
    public static ExtractResult Convert(Silo.Core.ExtractResult got, string path)
    {
        var r = new ExtractResult { Title = got.Title, Author = got.Author };
        Paged(r, got.Text ?? "");
        if (got.Model is { } m)
        {
            var software = string.Join(" ", new[] { m.Vendor, m.Product }.Where(s => !string.IsNullOrWhiteSpace(s)));
            r.Fields.Add("software", software);
            r.Fields.Add("model_kind", m.Kind);
            if (m.FsMin is double fs) r.Fields.Add("fs_min", fs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            if (m.ExpectsResults || m.HasResults) r.Fields.Add("has_results", m.HasResults ? "yes" : "no");
            r.Fields.Add("units", m.Units);
            foreach (var mat in m.Materials.Take(200)) r.Entities.Add(new Entity("material", mat));
            foreach (var st in m.Stages.Take(200)) r.Entities.Add(new Entity("stage", st));
            var summary = string.IsNullOrWhiteSpace(m.Summary) ? m.Kind : m.Summary;
            var extra = m.Chips.Where(c => !summary.Contains(c, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            r.Summary = extra.Count > 0 ? summary + " · " + string.Join(" · ", extra) : summary;
            if (r.Pages.Count == 0) Paged(r, m.SearchText(Path.GetFileName(path)));
        }
        if (got.WantsOcr) r.Warnings.Add("the file may hold text only a picture shows");
        r.TotalPages = r.Pages.Count;
        return r;
    }

    static void Paged(ExtractResult r, string text)
    {
        int n = r.Pages.Count, pos = 0;
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
}
