using Silo.Errors;
using System.Net;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.StaticFiles;
using Silo.Server.Core;
using Silo.Server.Index;
using Silo.Server.Pipeline;

namespace Silo.Server.Api;

/// <summary>
/// The HTTP API (/api/v1). The web front end is a thin client over it; an MCP layer or other clients can
/// sit on the same endpoints. Every read is trimmed to the caller's permissions.
/// </summary>
public static class ApiRoutes
{
    public static Principal Who(HttpContext http, SettingsStore settings)
    {
        var tokens = new List<string> { "public", "S-1-1-0" };
        string name = "anonymous";
        bool admin = IsLocal(http);
        if (OperatingSystem.IsWindows() && http.User.Identity is WindowsIdentity wi && wi.IsAuthenticated)
        {
            name = wi.Name;
            tokens.Add("S-1-5-11");   // Authenticated Users
            if (wi.User != null) tokens.Add(wi.User.Value);
            foreach (var g in wi.Groups ?? new IdentityReferenceCollection()) tokens.Add(g.Value);
            admin |= tokens.Contains("S-1-5-32-544");
        }
        return new Principal { Name = name, Tokens = tokens.Distinct().ToList(), IsAdmin = admin };
    }

    /// <summary>
    /// The server's own browser: the connection comes from this machine, was not passed on by a proxy, and was made
    /// to "localhost" by name. The last check matters: a web page elsewhere can point its own host name at 127.0.0.1
    /// (DNS rebinding), and a reverse proxy on this machine makes every visitor look local (0.1.4).
    /// </summary>
    static bool IsLocal(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip != null && !IPAddress.IsLoopback(ip)) return false;
        var h = http.Request.Headers;
        if (h.ContainsKey("X-Forwarded-For") || h.ContainsKey("Forwarded") || h.ContainsKey("X-Forwarded-Host") || h.ContainsKey("X-Real-IP")) return false;
        return http.Request.Host.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    public static bool IsLoopbackUrl(string url)
    {
        var host = Uri.TryCreate(url.Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0"), UriKind.Absolute, out var u) ? u.Host : "";
        return host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    /// <summary>
    /// How a file is sent. Only PDFs, plain pictures and plain text are shown in the browser; everything else is a
    /// download. An HTML or SVG file shown as a page would run its script as Silo itself, with the viewer's rights.
    /// </summary>
    public static (string type, bool inline) Delivery(string contentType, string ext)
    {
        ext = ext.TrimStart('.').ToLowerInvariant();
        if (contentType == "application/pdf") return (contentType, true);
        if (ext is "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "tif" or "tiff") return (contentType, true);
        if (contentType.StartsWith("text/") && ext is not ("htm" or "html" or "xhtml" or "xht" or "xml" or "svg" or "xsl" or "xslt" or "mht" or "mhtml" or "shtml"))
            return ("text/plain; charset=utf-8", true);
        return ("application/octet-stream", false);
    }

    public static void Map(WebApplication app)
    {
        var settings = app.Services.GetRequiredService<SettingsStore>();
        var api = app.MapGroup("/api/v1");

        api.MapGet("/", () => Results.Json(new
        {
            name = "Silo Server", version = typeof(ApiRoutes).Assembly.GetName().Version?.ToString(3), contract = Silo.Plugins.Contract.Version,
            endpoints = new object[]
            {
                new { method = "GET|POST", path = "/api/v1/search", about = "Search. q (query syntax), filters {dim:[values]}, not, yearFrom, yearTo, bbox [w,s,e,n], sort, offset, limit, group." },
                new { method = "GET", path = "/api/v1/suggest?q=", about = "Typing suggestions: tenements, commodities, companies, report titles." },
                new { method = "POST", path = "/api/v1/map?zoom=", about = "Matching reports as map points (clustered by zoom)." },
                new { method = "GET", path = "/api/v1/items/{id}", about = "One file's details and the other files of its report." },
                new { method = "GET", path = "/api/v1/items/{id}/pages/{n}?q=", about = "One page's text with the query highlighted." },
                new { method = "GET", path = "/api/v1/items/{id}/file", about = "The file itself (inline)." },
                new { method = "GET", path = "/api/v1/footprint?ds=&coll=", about = "A report's footprint as GeoJSON." },
                new { method = "GET", path = "/api/v1/overview", about = "Counts for the home page." },
                new { method = "GET", path = "/api/v1/syntax", about = "Query operators." },
            },
        }));

        api.MapGet("/syntax", () => Results.Json(new
        {
            operators = QueryParser.Operators.GroupBy(o => o.Value.dim).Select(g => new { names = g.Select(x => x.Key).ToList(), label = g.First().Value.label }),
            other = new[] { "year:1995", "year:1990..2005", "year:>2010", "before:2000", "after:2015", "ext:xlsx", "report:A59033", "has:location", "ocr:yes", "in:w,s,e,n", "x.<plugin field>:value", "\"exact phrase\"", "-exclude", "a OR b" },
        }));

        api.MapGet("/search", (HttpContext http, Searcher s, Engine e) =>
        {
            var q = http.Request.Query;
            var req = new SearchRequest
            {
                Q = q["q"].ToString(),
                Offset = int.TryParse(q["offset"], out var o) ? o : 0,
                Limit = Math.Clamp(int.TryParse(q["limit"], out var l) ? l : 10, 1, 100),
                Sort = q["sort"].FirstOrDefault() ?? "relevance",
                Group = q["group"] != "false",
                Filters = q.Keys.Where(k => k.StartsWith("f.") || k.StartsWith("x.")).ToDictionary(k => k, k => q[k].Where(v => v != null).Select(v => v!).ToList()),
            };
            if (q["bbox"].FirstOrDefault() is { } bb && QueryParser.TryBbox(bb, out var b)) req.Bbox = b;
            return Results.Json(s.Search(req, Who(http, settings), http.RequestAborted));
        });
        api.MapPost("/search", (HttpContext http, SearchRequest req, Searcher s) =>
        {
            req.Limit = Math.Clamp(req.Limit, 1, 100);
            return Results.Json(s.Search(req, Who(http, settings), http.RequestAborted));
        });

        api.MapGet("/suggest", (HttpContext http, string q, Searcher s) => Results.Json(s.Suggest(q, Who(http, settings))));

        api.MapPost("/map", (HttpContext http, SearchRequest req, int? zoom, Searcher s) =>
        {
            var (pts, located, total) = s.Map(req, Who(http, settings), zoom ?? 4);
            return Results.Json(new { points = pts.Select(p => new object?[] { p.Lat, p.Lon, p.N, p.Key, p.Id, p.Bbox }), located, total });
        });

        api.MapGet("/items/{id:long}", (HttpContext http, long id, Searcher s, Catalog cat) =>
        {
            var who = Who(http, settings);
            var hit = s.Get(id, who);
            if (hit == null) return Results.NotFound();
            var ds = hit.Card?["dsid"]?.ToString(); var coll = hit.Card?["coll"]?.ToString();
            var files = coll != null && ds != null ? s.Collection(ds + ":" + coll, who) : new List<Hit>();
            JsonNode? footprint = null;
            if (coll != null && ds != null) footprint = cat.Enrichments(ds, coll).Select(e => e.Footprint).FirstOrDefault(f => f != null);
            return Results.Json(new { hit.Id, hit.Card, files = files.Select(f => new { f.Id, f.Card }), footprint });
        });

        api.MapGet("/items/{id:long}/pages/{n:int}", (HttpContext http, long id, int n, string? q, Searcher s) =>
        {
            if (s.Get(id, Who(http, settings)) == null) return Results.NotFound();
            var page = s.PageText(id, n, q ?? "");
            if (page == null) return Results.Json(new { page = n, pages = 0, html = "", matches = Array.Empty<int>() });
            return Results.Json(new { page = page.Value.page, pages = page.Value.pages, html = page.Value.html, matches = string.IsNullOrWhiteSpace(q) ? new List<int>() : s.MatchingPages(id, q).OrderBy(x => x).ToList() });
        });

        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".rs3compute"] = "text/plain"; types.Mappings[".las"] = "text/plain"; types.Mappings[".log"] = "text/plain";
        api.MapGet("/items/{id:long}/file", (HttpContext http, long id, Searcher s, Catalog cat) =>
        {
            var hit = s.Get(id, Who(http, settings));
            if (hit == null) return Results.NotFound();
            var row = cat.Load(id);
            if (row?.Path == null || !File.Exists(row.Path)) return Fail.Result(Err.Srv.Api.FileNotReachable);
            if (!types.TryGetContentType(row.Path, out var ct)) ct = "application/octet-stream";
            var (type, inline) = Delivery(ct, Path.GetExtension(row.Path));
            // Whatever the file is, it is never a page of ours: no script, no forms, no requests. (PDFs carry no policy: the browser's own viewer must run, and a PDF cannot script this site.)
            if (type == "application/pdf") http.Response.Headers.Remove("Content-Security-Policy");
            else http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; sandbox";
            var stream = new FileStream(row.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);
            return Results.File(stream, type, inline ? null : row.Name, enableRangeProcessing: true);
        });

        api.MapGet("/footprint", (HttpContext http, string ds, string coll, Catalog cat, Searcher s) =>
        {
            // Only for a report the caller can see at least one file of.
            if (s.Collection(ds + ":" + coll, Who(http, settings)).Count == 0) return Results.NotFound();
            var f = cat.Enrichments(ds, coll).Select(e => e.Footprint).FirstOrDefault(x => x != null);
            return f == null ? Results.NotFound() : Results.Json(f);
        });

        api.MapGet("/overview", (HttpContext http, Searcher s, SearchIndex ix, Engine e, Catalog cat) =>
        {
            var r = s.Search(new SearchRequest { Q = "", Limit = 1 }, Who(http, settings));
            return Results.Json(new
            {
                files = r.Total, reports = r.Reports, building = e.IndexBuilding,
                pending = cat.Count("state IN (0,2)"),
                sources = r.Facets.GetValueOrDefault(F.DDataset), commodities = r.Facets.GetValueOrDefault(F.DCommodity),
                types = r.Facets.GetValueOrDefault(F.DRType), years = r.Facets.GetValueOrDefault(F.DYear), geo = r.Facets.GetValueOrDefault(F.DGeo),
                labels = Labels(app.Services.GetRequiredService<PluginManager>()),
            });
        });

        api.MapGet("/labels", (PluginManager pm) => Results.Json(Labels(pm)));

        AdminRoutes.Map(api, settings);
    }

    /// <summary>Display names for every facet dimension, including plugin fields.</summary>
    public static Dictionary<string, string> Labels(PluginManager pm)
    {
        var d = new Dictionary<string, string>
        {
            [F.DDataset] = "Source", [F.DCommodity] = "Commodity", [F.DRType] = "Report type", [F.DYear] = "Year", [F.DState] = "State",
            [F.DTenement] = "Tenement", [F.DCompany] = "Company", [F.DOwner] = "Company now (ASX)", [F.DAsx] = "ASX code", [F.DLith] = "Rock type",
            [F.DMethod] = "Method", [F.DFType] = "File type", [F.DGeo] = "Location",
        };
        foreach (var (k, v) in pm.DeclaredFields()) if (v.spec.Facet) d[k] = v.spec.Label;
        return d;
    }
}
