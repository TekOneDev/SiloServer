using System.Text.Json.Nodes;
using Silo.Server.Core;
using Silo.Server.Index;
using Silo.Server.Pipeline;

namespace Silo.Server.Api;

/// <summary>Management endpoints (/api/v1/admin). Only for this machine's own browser, or Windows administrators.</summary>
public static class AdminRoutes
{
    public static void Map(RouteGroupBuilder api, SettingsStore settings)
    {
        var admin = api.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
        {
            var who = ApiRoutes.Who(ctx.HttpContext, settings);
            if (!who.IsAdmin) return Results.Json(new { error = "Management is available on the server itself, or to Windows administrators." }, statusCode: 403);
            // Writes must come from our own pages (blocks cross-site form posts).
            if (!HttpMethods.IsGet(ctx.HttpContext.Request.Method) && ctx.HttpContext.Request.Headers["X-Silo"].FirstOrDefault() != "1")
                return Results.Json(new { error = "missing X-Silo header" }, statusCode: 400);
            return await next(ctx);
        });

        admin.MapGet("/status", (Engine e, Catalog cat, SearchIndex ix, PluginManager pm) =>
        {
            var st = cat.CountByState();
            var (er, or, ir) = e.Stats.Rates();
            var s = e.Stats;
            return Results.Json(new
            {
                version = typeof(AdminRoutes).Assembly.GetName().Version?.ToString(3),
                started = s.Started, paused = s.Paused, building = e.IndexBuilding,
                machine = Environment.MachineName, cores = Environment.ProcessorCount,
                memoryMb = Environment.WorkingSet / 1048576,
                dataDir = settings.DataDir, ocr = s.OcrStatus,
                items = st.Values.Sum(), pending = st.GetValueOrDefault(ItemState.Pending), needsOcr = st.GetValueOrDefault(ItemState.NeedsOcr),
                done = st.GetValueOrDefault(ItemState.Done), metadataOnly = st.GetValueOrDefault(ItemState.MetadataOnly), failed = st.GetValueOrDefault(ItemState.Failed),
                dirty = cat.DirtyCount(), indexDocs = ix.DocCount, indexPages = ix.PageCount, indexBytes = ix.SizeOnDisk(),
                catalogBytes = new FileInfo(Path.Combine(settings.DataDir, "catalog.db")).Length,
                rates = new { extractPerMin = Math.Round(er, 1), ocrPagesPerMin = Math.Round(or, 1), indexPerMin = Math.Round(ir, 1) },
                totals = new { s.Scanned, s.Extracted, s.ExtractFailed, s.OcrFiles, s.OcrPages, s.OcrRotated, s.Indexed, s.Enriched, s.EnrichGeo, reportsLocated = cat.EnrichCount(true), reportsEnriched = cat.EnrichCount(false) },
                activity = s.Activity.ToDictionary(kv => kv.Key, kv => kv.Value),
                sources = settings.Current.Sources.Select(src =>
                {
                    var (cp, last, count, status) = cat.SourceInfo(src.Id);
                    return new { src.Id, src.Name, src.Plugin, src.Enabled, src.Config, src.RescanMinutes, lastScan = last, count, status };
                }),
                types = cat.CountByType().Select(t => new { t.source, t.ext, t.n, t.bytes }),
                errors = cat.RecentErrors(30).Select(x => new { x.id, x.name, x.error }),
            });
        });

        admin.MapGet("/plugins", (PluginManager pm) => Results.Json(pm.All.Select(p => new
        {
            p.Id, p.Manifest.Name, p.Manifest.Version, p.Manifest.Kind, p.Manifest.Description, p.Manifest.Publisher, p.Manifest.Contract,
            p.Manifest.Extensions, p.Manifest.Names, p.Manifest.Datasets, p.Manifest.Concurrency, p.Manifest.TimeoutSec, p.Manifest.MaxFileMB,
            fields = p.Manifest.Fields, p.Enabled, p.Problem, p.Dir, calls = p.Calls, failures = p.Failures, timeouts = p.Timeouts, restarts = p.Restarts,
            avgMs = p.Calls > 0 ? Math.Round(p.TotalMs / p.Calls, 1) : 0, template = p.Manifest.ConfigTemplate,
        })));

        admin.MapPost("/plugins/{id}/enabled", (string id, bool on, PluginManager pm, Engine e) =>
        {
            settings.Update(s => { s.DisabledPlugins.Remove(id); if (!on) s.DisabledPlugins.Add(id); });
            var p = pm.Get(id); if (p != null) p.Enabled = on && p.Problem == null;
            return Results.Ok(new { ok = true, note = "Restart the service to re-read files with a newly enabled filter." });
        });

        admin.MapPost("/sources", (SourceConfig src, Engine e) =>
        {
            if (string.IsNullOrWhiteSpace(src.Name)) return Results.BadRequest(new { error = "name is required" });
            if (string.IsNullOrWhiteSpace(src.Id)) src.Id = new string(src.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            if (src.Config["root"]?.GetValue<string>() is { } root && src.Plugin == "silo.source.folder" && !Directory.Exists(root))
                return Results.BadRequest(new { error = $"The service cannot see the folder {root}. Check the path, and that the service account can read it." });
            settings.Update(s => { s.Sources.RemoveAll(x => x.Id == src.Id); s.Sources.Add(src); });
            e.RequestScan(src.Id);
            return Results.Ok(new { ok = true, id = src.Id });
        });

        admin.MapDelete("/sources/{id}", (string id, Catalog cat) =>
        {
            settings.Update(s => s.Sources.RemoveAll(x => x.Id == id));
            int n = cat.RemoveSource(id);
            return Results.Ok(new { ok = true, removed = n });
        });

        admin.MapPost("/scan", (string? source, Engine e) => { e.RequestScan(source); return Results.Ok(new { ok = true }); });
        admin.MapPost("/pause", (bool on, Engine e) => { e.Stats.Paused = on; return Results.Ok(new { ok = true, paused = on }); });
        admin.MapPost("/reindex", (Catalog cat, Engine e) => { cat.MarkDirty("1=1"); e.IndexBuilding = true; return Results.Ok(new { ok = true }); });
        admin.MapPost("/retry-failed", (Catalog cat) => Results.Ok(new { ok = true, n = cat.Requeue("state=9") }));
        admin.MapPost("/reextract", (string? ext, Catalog cat) => Results.Ok(new { ok = true, n = cat.Requeue(ext == null ? "1=1" : $"ext='{ext.Replace("'", "''").ToLowerInvariant()}'") }));
        admin.MapPost("/rules/reload", (Rules rules, Catalog cat) =>
        {
            var before = rules.Fingerprint; rules.Load();
            if (rules.Fingerprint != before) { cat.Set("rules", rules.Fingerprint); cat.MarkDirty("1=1"); }
            return Results.Ok(new { ok = true, changed = rules.Fingerprint != before, path = rules.Path });
        });
        admin.MapGet("/log", (Log log) => Results.Json(log.Recent.Take(300)));
        admin.MapGet("/settings", () => Results.Json(settings.Current));
        admin.MapPost("/settings", (JsonObject patch) =>
        {
            settings.Update(s =>
            {
                if (patch["ocr"] is JsonValue o) s.Ocr = o.GetValue<bool>();
                if (patch["enrich"] is JsonValue en) s.Enrich = en.GetValue<bool>();
                if (patch["extractWorkers"] is JsonValue ew) s.ExtractWorkers = ew.GetValue<int>();
                if (patch["ocrWorkers"] is JsonValue ow) s.OcrWorkers = ow.GetValue<int>();
                if (patch["auth"] is JsonValue au) s.Auth = au.GetValue<string>();
                if (patch["listen"] is JsonArray li) s.Listen = li.Select(x => x!.GetValue<string>()).ToList();
            });
            return Results.Ok(new { ok = true, note = "Worker counts, sign-in and listen addresses apply after a service restart." });
        });
    }
}
