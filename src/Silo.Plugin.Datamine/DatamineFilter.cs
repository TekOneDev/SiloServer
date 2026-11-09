using System.Globalization;
using System.Text;

namespace Silo.Plugins.Datamine;

/// <summary>
/// Datamine Studio binary tables (.dm): file type (block model, wireframe, strings, drillholes...),
/// fields, record count, extents and block size from the header; small files are scanned for value
/// lists (BHID, ROCK, DOMAIN...) and coordinate ranges. Reads in place, header first, never the whole
/// of a big block model.
/// </summary>
public sealed class DatamineFilter : FilterPlugin
{
    public override PluginManifest Manifest { get; } = new()
    {
        Id = "silo.filter.datamine",
        Name = "Datamine Studio files",
        Version = "0.9.0",
        Kind = "filter",
        Description = "Datamine .dm tables: block models, wireframes, strings and drillholes (header and extents).",
        Publisher = "Silo",
        Extensions = [".dm"],
        Concurrency = 8,
        TimeoutSec = 300,
        MaxFileMB = 1_000_000,
        ReadsInPlace = true,
        Fields =
        [
            new FieldSpec { Name = "dm_type", Label = "Datamine file type", Type = "keyword", Facet = true, Show = true },
            new FieldSpec { Name = "dm_fields", Label = "Datamine fields", Type = "text", Facet = false },
            new FieldSpec { Name = "dm_records", Label = "Records", Type = "number", Facet = false, Show = true },
            new FieldSpec { Name = "dm_block_size", Label = "Block size", Type = "keyword", Facet = false, Show = true },
            new FieldSpec { Name = "software", Label = "Software", Type = "keyword", Facet = true, Show = true },
        ],
    };

    const long ScanMax = 64L * 1024 * 1024;

    public override ExtractResult Extract(ExtractArgs a, CancellationToken ct)
    {
        var r = new ExtractResult();
        var info = DmProbe.Probe(a.Path, ScanMax, false);
        r.Fields.Add("software", "Datamine Studio");
        if (info.Status != "ok")
        {
            r.Summary = "Datamine file (not recognised: " + info.Note + ")";
            r.Warnings.Add(info.Note ?? "unrecognised");
            return r;
        }
        r.Fields.Add("dm_type", Cap(info.FileType));
        r.Fields.Add("dm_records", info.Records?.ToString(CultureInfo.InvariantCulture));
        r.Fields.Add("dm_fields", string.Join(" ", info.Fields.Select(f => f.Name)));
        if (info.BlockSize != null) r.Fields.Add("dm_block_size", info.BlockSize.Replace("x", " × "));
        if (!string.IsNullOrWhiteSpace(info.Description)) r.Title = info.Description;

        var sb = new StringBuilder();
        sb.AppendLine($"Datamine {info.FileType} ({info.Precision} precision), {info.Records:N0} records, {info.FieldCount} fields.");
        if (!string.IsNullOrWhiteSpace(info.Description)) sb.AppendLine("Description: " + info.Description);
        if (!string.IsNullOrWhiteSpace(info.InternalName)) sb.AppendLine("Table: " + info.InternalName + " " + info.Database);
        if (info.Extents != null) sb.AppendLine("Extents (x,y,z min, x,y,z max): " + info.Extents);
        if (info.BlockSize != null) sb.AppendLine($"Blocks: {info.BlockCounts} of {info.BlockSize}");
        sb.AppendLine("Fields:");
        foreach (var f in info.Fields)
        {
            sb.Append("  ").Append(f.Name).Append(f.Type == "A" ? " (text)" : " (number)");
            if (f.Implicit && f.Default.Length > 0) sb.Append(" = ").Append(f.Default);
            if (f.Min.HasValue) sb.Append($" range {f.Min:G6} to {f.Max:G6}");
            sb.AppendLine();
            if (f.Values is { Length: > 0 })
            {
                sb.Append("    values: ").AppendLine(string.Join(", ", f.Values.Take(200)));
                var etype = f.Name.ToUpperInvariant() == "BHID" ? "hole" : "dm_" + f.Name.ToLowerInvariant();
                foreach (var v in f.Values.Take(500)) r.Entities.Add(new Entity(etype, v));
            }
        }
        r.Pages.Add(new PageText { N = 1, Text = sb.ToString() });
        r.TotalPages = 1;

        string sum = Cap(info.FileType);
        if (info.BlockCounts != null) sum += $" {info.BlockCounts.Replace("x", "×")} blocks of {info.BlockSize!.Replace("x", "×")} m";
        sum += $" · {info.Records:N0} records · {info.FieldCount} fields";
        r.Summary = sum;
        if (info.Extents != null)
        {
            var e = info.Extents.Split(',').Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
            if (e.Length == 6 && e.All(double.IsFinite))
                r.Geo = new Geo { Raw = $"local/projected {e[0]:F0}E {e[1]:F0}N to {e[3]:F0}E {e[4]:F0}N, RL {e[2]:F0} to {e[5]:F0}", Basis = "model-extents" };
        }
        return r;
    }

    static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
