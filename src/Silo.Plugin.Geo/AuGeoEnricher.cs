using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace Silo.Plugins.AuGeo;

/// <summary>
/// Report footprints for Australian open-file report libraries, looked up once per report:
///   wamex  WA DEMIRS SLIP layer (Mineral exploration reports, MapServer/22), by A-number
///   gsq    Geological Survey of Queensland CKAN portal, the dataset's "spatial" extent
/// Returns the footprint (simplified GeoJSON), its bounding box and centre.
/// </summary>
public sealed class AuGeoEnricher : EnricherPlugin
{
    public override PluginManifest Manifest { get; } = new()
    {
        Id = "silo.enrich.au-geo",
        Name = "Report locations (WAMEX, GSQ)",
        Version = "1.0.0",
        Kind = "enricher",
        Description = "Looks up report footprints from the WA SLIP map service and the Queensland geoscience portal.",
        Publisher = "Silo",
        Datasets = ["wamex", "gsq"],
        Concurrency = 2,
        TimeoutSec = 180,
    };

    const string WamexLayer = "https://public-services.slip.wa.gov.au/public/rest/services/SLIP_Public_Services/Industry_and_Mining/MapServer/22";
    const string GsqBase = "https://geoscience.data.qld.gov.au";

    static readonly HttpClient Http = MakeHttp();

    static HttpClient MakeHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseProxy = true }) { Timeout = TimeSpan.FromSeconds(60) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("SiloServer/0.1 (report locations; open data)");
        return h;
    }

    public override EnrichResult Enrich(EnrichArgs args, CancellationToken ct)
    {
        var res = new EnrichResult();
        var wamex = args.Targets.Where(t => t.Dataset == "wamex").ToList();
        for (int i = 0; i < wamex.Count; i += 100) res.Items.AddRange(Wamex(wamex.Skip(i).Take(100).ToList()));
        foreach (var t in args.Targets.Where(t => t.Dataset == "gsq")) { res.Items.Add(Gsq(t)); Thread.Sleep(150); }
        foreach (var t in args.Targets.Where(t => t.Dataset is not ("wamex" or "gsq")))
            res.Items.Add(new EnrichOutput { Dataset = t.Dataset, Collection = t.Collection, Final = true });
        return res;
    }

    // ---------- WAMEX ----------

    static List<EnrichOutput> Wamex(List<EnrichTarget> batch)
    {
        var outs = new Dictionary<string, EnrichOutput>(StringComparer.OrdinalIgnoreCase);
        var nums = new List<long>();
        foreach (var t in batch)
        {
            outs[t.Collection] = new EnrichOutput { Dataset = t.Dataset, Collection = t.Collection, Final = true };
            if (long.TryParse(t.Collection.TrimStart('A', 'a'), out var n)) nums.Add(n);
        }
        if (nums.Count == 0) return outs.Values.ToList();
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["where"] = $"anumber IN ({string.Join(",", nums)})",
                ["outFields"] = "anumber",
                ["returnGeometry"] = "true",
                ["outSR"] = "4326",
                ["maxAllowableOffset"] = "0.002",
                ["geometryPrecision"] = "5",
                ["f"] = "json",
            });
            var resp = Http.PostAsync(WamexLayer + "/query", form).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            var root = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            if (root?["error"] != null) throw new InvalidOperationException(root["error"]!.ToJsonString());
            // One report can have several polygons (several tenements): merge them.
            var rings = new Dictionary<string, List<List<double[]>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in root?["features"]?.AsArray() ?? new JsonArray())
            {
                var a = f?["attributes"]?["anumber"];
                if (a == null) continue;
                var key = "A" + Convert.ToInt64(double.Parse(a.ToString(), CultureInfo.InvariantCulture));
                if (!rings.TryGetValue(key, out var list)) rings[key] = list = new();
                foreach (var ring in f?["geometry"]?["rings"]?.AsArray() ?? new JsonArray())
                    list.Add(ring!.AsArray().Select(p => new[] { p![0]!.GetValue<double>(), p[1]!.GetValue<double>() }).ToList());
                if (f?["geometry"]?["x"] is JsonNode x && f["geometry"]?["y"] is JsonNode y)
                    list.Add([[x.GetValue<double>(), y.GetValue<double>()]]);
            }
            foreach (var (key, list) in rings)
            {
                var o = outs.Values.FirstOrDefault(v => v.Collection.Equals(key, StringComparison.OrdinalIgnoreCase)
                    || v.Collection.TrimStart('A', 'a').TrimStart('0') == key.TrimStart('A').TrimStart('0'));
                if (o == null) continue;
                Apply(o, list, "WAMEX report footprint");
            }
        }
        catch (Exception)
        {
            foreach (var o in outs.Values) o.Final = false;   // try again later
        }
        return outs.Values.ToList();
    }

    // ---------- GSQ ----------

    static EnrichOutput Gsq(EnrichTarget t)
    {
        var o = new EnrichOutput { Dataset = t.Dataset, Collection = t.Collection, Final = true };
        try
        {
            var json = Http.GetStringAsync($"{GsqBase}/api/3/action/package_show?id={Uri.EscapeDataString(t.Collection.ToLowerInvariant())}").GetAwaiter().GetResult();
            var pkg = JsonNode.Parse(json)?["result"];
            if (pkg == null) return o;
            JsonNode? geom = null;
            foreach (var cand in new[] { pkg["spatial"], pkg["spatial_geometry"], pkg["geometry"], pkg["bbox"] })
                if ((geom = AsGeometry(cand)) != null) break;
            if (geom == null)
                foreach (var e in pkg["extras"]?.AsArray() ?? new JsonArray())
                    if ((e?["key"]?.ToString() ?? "").Contains("spatial", StringComparison.OrdinalIgnoreCase) && (geom = AsGeometry(e?["value"])) != null) break;
            if (geom == null) return o;
            var rings = Rings(geom);
            if (rings.Count > 0) Apply(o, rings, "GSQ report extent");
            foreach (var key in new[] { "georesource_report_type", "resource_authority_permit", "permit" })
                if (pkg[key] is JsonNode v) foreach (var s in Strings(v)) o.Fields.Add(key == "georesource_report_type" ? "" : "tenement", s.Split('/').Last());
            o.Fields.Remove("");
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { }
        catch (Exception) { o.Final = false; }
        return o;
    }

    static IEnumerable<string> Strings(JsonNode n) => n is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0) : [n.ToString()];

    static JsonNode? AsGeometry(JsonNode? n)
    {
        if (n == null) return null;
        if (n is JsonValue v && v.TryGetValue<string>(out var s))
        {
            s = s.Trim();
            if (!s.StartsWith('{')) return null;
            try { n = JsonNode.Parse(s); } catch { return null; }
        }
        if (n is JsonObject o && o["type"] != null && (o["coordinates"] != null || o["geometries"] != null)) return o;
        return null;
    }

    static List<List<double[]>> Rings(JsonNode g)
    {
        var list = new List<List<double[]>>();
        var type = g["type"]?.ToString();
        var c = g["coordinates"];
        static double[] P(JsonNode? p) => [p![0]!.GetValue<double>(), p[1]!.GetValue<double>()];
        switch (type)
        {
            case "Point": list.Add([P(c)]); break;
            case "MultiPoint" or "LineString": list.Add(c!.AsArray().Select(P).ToList()); break;
            case "Polygon" or "MultiLineString": foreach (var r in c!.AsArray()) list.Add(r!.AsArray().Select(P).ToList()); break;
            case "MultiPolygon": foreach (var poly in c!.AsArray()) foreach (var r in poly!.AsArray()) list.Add(r!.AsArray().Select(P).ToList()); break;
            case "GeometryCollection": foreach (var gg in g["geometries"]!.AsArray()) list.AddRange(Rings(gg!)); break;
        }
        return list;
    }

    static void Apply(EnrichOutput o, List<List<double[]>> rings, string basis)
    {
        var pts = rings.SelectMany(r => r).Where(p => Math.Abs(p[0]) <= 180 && Math.Abs(p[1]) <= 90).ToList();
        if (pts.Count == 0) return;
        double w = pts.Min(p => p[0]), e = pts.Max(p => p[0]), s = pts.Min(p => p[1]), n = pts.Max(p => p[1]);
        o.Geo = new Silo.Plugins.Geo { Lat = (s + n) / 2, Lon = (w + e) / 2, Bbox = [w, s, e, n], Basis = basis };
        // Keep the footprint small: at most ~400 points in all.
        int total = rings.Sum(r => r.Count);
        int step = Math.Max(1, total / 400);
        var polys = new JsonArray();
        foreach (var r in rings.Take(40))
        {
            if (r.Count < 3) continue;
            var ring = new JsonArray();
            for (int i = 0; i < r.Count; i += step) ring.Add(new JsonArray(Math.Round(r[i][0], 5), Math.Round(r[i][1], 5)));
            ring.Add(new JsonArray(Math.Round(r[0][0], 5), Math.Round(r[0][1], 5)));
            if (ring.Count >= 4) polys.Add(new JsonArray(ring));
        }
        if (polys.Count > 0) o.Footprint = new JsonObject { ["type"] = "MultiPolygon", ["coordinates"] = polys };
    }
}
