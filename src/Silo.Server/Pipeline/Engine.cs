using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Silo.Plugins;
using Silo.Server.Core;
using Silo.Server.Index;

namespace Silo.Server.Pipeline;

/// <summary>Live counters for the admin page.</summary>
public sealed class Stats
{
    public long Scanned, Extracted, ExtractFailed, OcrFiles, OcrPages, OcrRotated, Indexed, Enriched, EnrichGeo;
    public double ExtractMs, OcrMs;
    public DateTime Started = DateTime.UtcNow;
    public readonly ConcurrentDictionary<string, string> Activity = new();
    public string? OcrStatus;
    public bool Paused;
    readonly ConcurrentQueue<(DateTime t, long extracted, long ocr, long indexed)> _samples = new();

    public void Sample()
    {
        _samples.Enqueue((DateTime.UtcNow, Extracted, OcrPages, Indexed));
        while (_samples.Count > 0 && _samples.TryPeek(out var s) && s.t < DateTime.UtcNow.AddMinutes(-5)) _samples.TryDequeue(out _);
    }

    /// <summary>Per-minute rates over the last few minutes: files extracted, pages OCR'd, documents indexed.</summary>
    public (double extract, double ocr, double index) Rates()
    {
        var a = _samples.ToArray();
        if (a.Length < 2) return (0, 0, 0);
        var mins = Math.Max(1e-6, (a[^1].t - a[0].t).TotalMinutes);
        return ((a[^1].extracted - a[0].extracted) / mins, (a[^1].ocr - a[0].ocr) / mins, (a[^1].indexed - a[0].indexed) / mins);
    }
}

/// <summary>
/// The indexing pipeline. Each stage is its own loop so search is useful early:
///   scan     sources list items (names and metadata are searchable within minutes)
///   extract  filter plugins read text by page
///   ocr      the core reads scanned pages
///   enrich   enricher plugins add report locations and other collection metadata
///   index    the catalog's change log is applied to the search index
/// </summary>
public sealed class Engine : BackgroundService
{
    readonly SettingsStore _settings;
    readonly Catalog _cat;
    readonly PluginManager _plugins;
    readonly Rules _rules;
    readonly SearchIndex _index;
    readonly Log _log;
    public Stats Stats { get; } = new();
    readonly ConcurrentDictionary<long, byte> _inflight = new();
    readonly ConcurrentDictionary<long, byte> _ocrInflight = new();
    readonly ConcurrentDictionary<string, PluginProcess> _watchers = new();
    readonly ConcurrentQueue<(string source, List<string> keys)> _events = new();
    readonly ConcurrentDictionary<string, bool> _scanRequests = new();
    readonly SemaphoreSlim _scanLock = new(1, 1);
    OcrPool? _ocr;
    public bool OcrAvailable { get; private set; }
    public volatile bool IndexBuilding;

    public Engine(SettingsStore settings, Catalog cat, PluginManager plugins, Rules rules, SearchIndex index, Log log)
    {
        _settings = settings; _cat = cat; _plugins = plugins; _rules = rules; _index = index; _log = log;
    }

    public void RequestScan(string? sourceId = null)
    {
        foreach (var s in _settings.Current.Sources.Where(s => sourceId == null || s.Id == sourceId)) _scanRequests[s.Id] = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Yield();
        Prepare();
        var loops = new[]
        {
            Loop("scan", ScanLoop, stop), Loop("extract", ExtractLoop, stop), Loop("ocr", OcrLoop, stop),
            Loop("enrich", EnrichLoop, stop), Loop("index", IndexLoop, stop), Loop("stats", StatsLoop, stop),
        };
        await Task.WhenAll(loops);
    }

    Task Loop(string name, Func<CancellationToken, Task> body, CancellationToken stop) => Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            try { await body(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e) { _log.Error($"{name} loop: {e.GetType().Name}: {e.Message}"); try { await Task.Delay(5000, stop); } catch { return; } }
        }
    }, stop);

    void Prepare()
    {
        // Reindex everything when the index schema or the rules changed.
        if (_index.Fresh) { _log.Info("New search index: rebuilding it from the catalog."); _cat.MarkDirty("1=1"); }
        var fp = _rules.Fingerprint;
        if (_cat.Get("rules") != fp)
        {
            if (_cat.Get("rules") != null && !_index.Fresh) { _log.Info("Rules changed: reindexing."); _cat.MarkDirty("1=1"); }
            _cat.Set("rules", fp);
        }
        // A new or upgraded filter plugin re-reads the files it handles.
        foreach (var p in _plugins.All.Where(p => p.Manifest.Kind == "filter" && p.Enabled))
        {
            var tag = p.Id + "@" + p.Manifest.Version;
            int n = _cat.Requeue($"filter LIKE '{p.Id.Replace("'", "''")}@%' AND filter <> '{tag.Replace("'", "''")}'");
            if (n > 0) _log.Info($"{p.Manifest.Name} is now version {p.Manifest.Version}: re-reading {n:N0} files.");
            foreach (var ext in p.Manifest.Extensions.Where(e => e != "*"))
            {
                int m = _cat.Requeue($"state=4 AND filter IS NULL AND ext='{ext.Replace("'", "''")}'");
                if (m > 0) _log.Info($"{p.Manifest.Name} can now read {ext} files: reading {m:N0}.");
            }
        }
        IndexBuilding = _cat.DirtyCount() > 5000;
        if (_settings.Current.Ocr)
        {
            var why = OcrEngine.Check();
            OcrAvailable = why == null;
            Stats.OcrStatus = OcrAvailable ? $"on ({OcrEngine.Language})" : $"off: {why}";
            if (OcrAvailable) _ocr = new OcrPool(_settings.OcrWorkers, _log);
        }
        else Stats.OcrStatus = "turned off in settings";
        _log.Info($"Pipeline ready: {_settings.ExtractWorkers} extraction workers, OCR {Stats.OcrStatus}.");
        RequestScan();
    }

    // ---------- scan ----------

    async Task ScanLoop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            foreach (var src in _settings.Current.Sources.Where(s => s.Enabled).ToList())
            {
                var (_, last, _, _) = _cat.SourceInfo(src.Id);
                bool asked = _scanRequests.TryRemove(src.Id, out _);
                bool backoff = _scanRetry.TryGetValue(src.Id, out var retryAt) && DateTime.UtcNow < retryAt;
                bool due = asked || (!backoff && (last == null || Catalog.Now - last > src.RescanMinutes * 60_000L));
                if (due && !Stats.Paused) await Task.Run(() => Scan(src, stop), stop);
                StartWatch(src);
            }
            while (_events.TryDequeue(out var ev)) await Task.Run(() => HandleEvent(ev.source, ev.keys), stop);
            await Task.Delay(2000, stop);
        }
    }

    JsonObject SourceArgs(SourceConfig src, JsonObject? extra = null)
    {
        var cfg = (JsonObject)src.Config.DeepClone();
        if (extra != null) foreach (var (k, v) in extra) cfg[k] = v?.DeepClone();
        return new JsonObject { ["sourceId"] = src.Id, ["config"] = cfg, ["checkpoint"] = _cat.SourceInfo(src.Id).checkpoint };
    }

    void Scan(SourceConfig src, CancellationToken stop, string? subpath = null)
    {
        var info = _plugins.Get(src.Plugin);
        if (info == null || !info.Enabled) { _cat.SourceStatus(src.Id, $"plugin {src.Plugin} not available"); return; }
        _scanLock.Wait(stop);
        try
        {
            var sw = Stopwatch.StartNew();
            Stats.Activity["scan"] = $"Listing {src.Name}" + (subpath != null ? $" ({subpath})" : "");
            int gen = _cat.NextGeneration(src.Id);
            var batch = new List<SourceItem>(1000);
            long seen = 0, changed = 0;
            using var proc = _plugins.Process(info);
            var args = SourceArgs(src, subpath == null ? null : new JsonObject { ["subpath"] = subpath });
            var res = proc.Call("enumerate", args, TimeSpan.FromMinutes(30), item =>
            {
                batch.Add(item); seen++;
                if (batch.Count >= 1000) { changed += _cat.Upsert(src.Id, gen, batch); batch.Clear(); Stats.Activity["scan"] = $"Listing {src.Name}: {seen:N0} items"; Interlocked.Add(ref Stats.Scanned, 1000); }
            }, stop);
            changed += _cat.Upsert(src.Id, gen, batch);
            var er = res?.Deserialize<EnumerateResult>(Contract.Json) ?? new EnumerateResult();
            int removed = 0;
            if (subpath == null && er.Complete) removed = _cat.RemoveUnseen(src.Id, gen);
            else if (subpath != null) removed = _cat.RemoveUnseenUnder(src.Id, subpath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, gen);
            if (subpath == null) { _cat.SourceScanned(src.Id, er.Checkpoint, seen, $"ok: {seen:N0} items"); _scanRetry.TryRemove(src.Id, out _); }
            if (changed + removed > 0 || subpath == null) _log.Info($"{src.Name}{(subpath != null ? " / " + subpath : "")}: {seen:N0} items, {changed:N0} new or changed, {removed:N0} removed ({sw.Elapsed.TotalSeconds:N0}s)");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _cat.SourceStatus(src.Id, "error: " + e.Message);
            _scanRetry[src.Id] = DateTime.UtcNow.AddMinutes(5);
            _log.Warn($"{src.Name}: scan failed (retrying in 5 minutes): {e.Message}");
        }
        finally { Stats.Activity.TryRemove("scan", out _); _scanLock.Release(); }
    }

    readonly ConcurrentDictionary<string, DateTime> _watchRetry = new();
    readonly ConcurrentDictionary<string, DateTime> _scanRetry = new();

    void StartWatch(SourceConfig src)
    {
        if (_watchers.TryGetValue(src.Id, out var w) && w.Running) return;
        if (_watchRetry.TryGetValue(src.Id, out var next) && DateTime.UtcNow < next) return;
        _watchRetry[src.Id] = DateTime.UtcNow.AddMinutes(5);
        var info = _plugins.Get(src.Plugin);
        if (info == null || !info.Enabled) return;
        try
        {
            w?.Dispose();
            var p = _plugins.Process(info);
            p.OnEvent = r => { if (r.Event == "changed" && r.Keys != null) _events.Enqueue((src.Id, r.Keys)); };
            var res = p.Call("watch", SourceArgs(src), TimeSpan.FromSeconds(60));
            if (res?["watching"]?.GetValue<bool>() == true) _watchers[src.Id] = p;
            else { p.Dispose(); _watchers.TryRemove(src.Id, out _); }
        }
        catch (Exception e) { _log.Debug($"watch {src.Id}: {e.Message}"); }
    }

    void HandleEvent(string sourceId, List<string> keys)
    {
        var src = _settings.Current.Sources.FirstOrDefault(s => s.Id == sourceId && s.Enabled);
        if (src == null) return;
        if (keys.Contains("*") || keys.Count > 500) { RequestScan(sourceId); return; }
        var folders = keys.Where(k => k.EndsWith("*")).Select(k => k.TrimEnd('*').TrimEnd('\\', '/')).Distinct().ToList();
        foreach (var f in folders) Scan(src, CancellationToken.None, f.Length == 0 || f == "." ? null : f);
        var files = keys.Where(k => !k.EndsWith("*")).Where(k => !folders.Any(f => k.StartsWith(f + Path.DirectorySeparatorChar))).ToList();
        if (files.Count == 0) return;
        var info = _plugins.Get(src.Plugin);
        if (info == null) return;
        using var p = _plugins.Process(info);
        int gen = _cat.NextGeneration(src.Id);
        var got = new List<SourceItem>();
        foreach (var k in files)
        {
            try
            {
                var args = SourceArgs(src); args["key"] = k;
                var r = p.Call("get", new JsonObject { ["source"] = args, ["key"] = k }, TimeSpan.FromSeconds(60));
                var item = r?.Deserialize<SourceItem>(Contract.Json);
                if (item == null) _cat.RemoveKey(src.Id, k); else got.Add(item);
            }
            catch (Exception e) { _log.Debug($"get {k}: {e.Message}"); }
        }
        _cat.Upsert(src.Id, gen, got);
    }

    // ---------- extract ----------

    async Task ExtractLoop(CancellationToken stop)
    {
        int workers = _settings.ExtractWorkers;
        using var slots = new SemaphoreSlim(workers, workers);
        while (!stop.IsCancellationRequested)
        {
            if (Stats.Paused) { await Task.Delay(2000, stop); continue; }
            var ids = _cat.NextIds(ItemState.Pending, workers * 8, _inflight.Keys.ToList());
            if (ids.Count == 0) { Stats.Activity.TryRemove("extract", out _); await Task.Delay(2000, stop); continue; }
            foreach (var id in ids)
            {
                await slots.WaitAsync(stop);
                _inflight[id] = 0;
                _ = Task.Run(() =>
                {
                    try { Extract(id, stop); }
                    catch (Exception e) when (e is not OperationCanceledException) { _cat.SetState(id, ItemState.Failed, e.Message); }
                    finally { _inflight.TryRemove(id, out _); slots.Release(); }
                }, stop);
            }
            // Wait for this round to drain a little before asking the catalog again.
            while (_inflight.Count > workers * 4) await Task.Delay(100, stop);
        }
    }

    void Extract(long id, CancellationToken stop)
    {
        var it = _cat.Load(id);
        if (it == null || it.State != ItemState.Pending) return;
        Stats.Activity["extract"] = $"Reading {it.Name}";
        if (it.Text != null || it.Path == null) { _cat.SaveExtract(id, ItemState.Done, null, null, null, null, 0, null); return; }
        var filter = _plugins.FilterFor(it.Name, it.Ext);
        if (filter == null) { _cat.SaveExtract(id, ItemState.MetadataOnly, null, null, null, null, 0, null); return; }
        var m = filter.Manifest;
        if (it.Size > m.MaxFileMB * 1024L * 1024L) { _cat.SaveExtract(id, ItemState.MetadataOnly, filter.Id + "@" + m.Version, null, null, $"larger than {m.MaxFileMB:N0} MB: name and details only", 0, null); return; }
        var pool = _plugins.Pool(filter, _settings.ExtractWorkers);
        var timeout = TimeSpan.FromSeconds(Math.Min(3600, m.TimeoutSec + (m.ReadsInPlace ? 0 : it.Size / 1_000_000.0 * 2)));
        var sw = Stopwatch.StartNew();
        ExtractResult? x = null; string? error = null;
        try
        {
            var args = new JsonObject { ["path"] = it.Path, ["ext"] = it.Ext ?? "", ["size"] = it.Size, ["maxPages"] = 5000, ["maxChars"] = 30_000_000 };
            x = pool.Call("extract", args, timeout, stop)?.Deserialize<ExtractResult>(Contract.Json);
        }
        catch (Exception e) when (e is not OperationCanceledException) { error = e.Message; }
        Interlocked.Increment(ref Stats.Extracted);
        lock (Stats) Stats.ExtractMs += sw.Elapsed.TotalMilliseconds;
        var tag = filter.Id + "@" + m.Version;
        if (x == null) { Interlocked.Increment(ref Stats.ExtractFailed); _cat.SaveExtract(id, ItemState.Failed, tag, null, null, error ?? "no result", 0, null); return; }
        var pages = x.Pages;
        int ocrWanted = x.OcrImage ? 1 : Math.Min(pages.Count(p => p.NeedsOcr), _settings.Current.MaxOcrPagesPerFile);
        bool ocr = ocrWanted > 0 && OcrAvailable && _settings.Current.Ocr;
        var warn = x.Warnings.Count > 0 ? string.Join("; ", x.Warnings.Take(3)) : null;
        _cat.SaveExtract(id, ocr ? ItemState.NeedsOcr : ItemState.Done, tag, x, pages, warn, ocr ? ocrWanted : 0, TextHash(pages));
    }

    public static string? TextHash(IReadOnlyList<PageText> pages)
    {
        long n = pages.Sum(p => (long)p.Text.Length);
        if (n < 200) return null;
        using var h = SHA256.Create();
        foreach (var p in pages) { var b = Encoding.UTF8.GetBytes(p.Text.ToLowerInvariant()); h.TransformBlock(b, 0, b.Length, null, 0); }
        h.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(h.Hash!)[..20];
    }

    // ---------- OCR ----------

    async Task OcrLoop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (_ocr == null || Stats.Paused) { await Task.Delay(5000, stop); continue; }
            // OCR is slow: let extraction get ahead first so every file is at least searchable by its text layer.
            var ids = _cat.NextIds(ItemState.NeedsOcr, _ocr.Size * 2, _ocrInflight.Keys.ToList());
            if (ids.Count == 0) { Stats.Activity.TryRemove("ocr", out _); await Task.Delay(3000, stop); continue; }
            var tasks = ids.Select(id => Task.Run(() =>
            {
                if (!_ocrInflight.TryAdd(id, 0)) return;
                try { Ocr(id, stop); }
                catch (Exception e) when (e is not OperationCanceledException) { _cat.SetState(id, ItemState.Done, "OCR failed: " + e.Message); }
                finally { _ocrInflight.TryRemove(id, out _); }
            }, stop)).ToList();
            await Task.WhenAll(tasks);
        }
    }

    void Ocr(long id, CancellationToken stop)
    {
        var it = _cat.Load(id);
        if (it == null || it.State != ItemState.NeedsOcr || it.Path == null) return;
        var pages = _cat.Pages(id);
        bool image = FileTypes.Group(it.Ext, it.Name) == "Image or scan";
        var want = pages.Where(p => p.NeedsOcr).Select(p => p.N).Take(_settings.Current.MaxOcrPagesPerFile).ToList();
        if (!image && want.Count == 0) { _cat.SaveOcr(id, pages, null, null); return; }
        Stats.Activity["ocr"] = $"OCR {it.Name}" + (image ? "" : $" ({want.Count} pages)");
        var sw = Stopwatch.StartNew();
        var reply = _ocr!.Run(new OcrJob { Path = it.Path, Image = image, Pages = want }, TimeSpan.FromSeconds(120 + 30 * Math.Max(1, want.Count)), stop);
        lock (Stats) Stats.OcrMs += sw.Elapsed.TotalMilliseconds;
        Interlocked.Increment(ref Stats.OcrFiles);
        Interlocked.Add(ref Stats.OcrPages, reply.Pages.Count);
        Interlocked.Add(ref Stats.OcrRotated, reply.Rotated);
        foreach (var (n, raw) in reply.Pages)
        {
            var text = OcrPool.Clean(raw);
            var p = pages.FirstOrDefault(x => x.N == n);
            if (p == null) { pages.Add(new PageText { N = n, Text = text, Ocr = true }); continue; }
            p.NeedsOcr = false;
            if (text.Length > p.Text.Length) { p.Text = text; p.Ocr = true; }
        }
        pages = pages.OrderBy(p => p.N).ToList();
        _cat.SaveOcr(id, pages, TextHash(pages), reply.Error);
    }

    // ---------- enrich ----------

    async Task EnrichLoop(CancellationToken stop)
    {
        await Task.Delay(5000, stop);
        while (!stop.IsCancellationRequested)
        {
            bool any = false;
            if (_settings.Current.Enrich && !Stats.Paused)
                foreach (var p in _plugins.Enrichers.ToList())
                {
                    var todo = _cat.CollectionsToEnrich(p.Id, p.Manifest.Datasets, 50);
                    if (todo.Count == 0) continue;
                    any = true;
                    Stats.Activity["enrich"] = $"{p.Manifest.Name}: {todo.Count} reports";
                    var args = new JsonObject { ["targets"] = JsonSerializer.SerializeToNode(todo.Select(t => new EnrichTarget { Dataset = t.dataset, Collection = t.collection, Meta = t.meta }).ToList(), Contract.Json) };
                    EnrichResult? res = null;
                    try { res = _plugins.Pool(p, 2).Call("enrich", args, TimeSpan.FromSeconds(p.Manifest.TimeoutSec), stop)?.Deserialize<EnrichResult>(Contract.Json); }
                    catch (Exception e) when (e is not OperationCanceledException) { _log.Warn($"{p.Manifest.Name}: {e.Message}"); }
                    var answered = new HashSet<(string, string)>();
                    foreach (var o in res?.Items ?? new())
                    {
                        _cat.SaveEnrich(p.Id, o);
                        answered.Add((o.Dataset, o.Collection));
                        if (o.Geo != null || o.Fields.Count > 0) { _cat.MarkCollectionDirty(o.Dataset, o.Collection); if (o.Geo != null) Interlocked.Increment(ref Stats.EnrichGeo); }
                        Interlocked.Increment(ref Stats.Enriched);
                    }
                    // Not answered at all (plugin failed): record a retryable miss so the loop moves on.
                    foreach (var t in todo.Where(t => !answered.Contains((t.dataset, t.collection))))
                        _cat.SaveEnrich(p.Id, new EnrichOutput { Dataset = t.dataset, Collection = t.collection, Final = false });
                    if (res == null) await Task.Delay(TimeSpan.FromMinutes(2), stop);
                }
            Stats.Activity.TryRemove("enrich", out _);
            await Task.Delay(any ? 500 : 15000, stop);
        }
    }

    // ---------- index ----------

    // The highest change number handed to the index writers but not yet committed. Change-log rows are deleted only
    // after the commit: a crash or forced stop in between used to drop those files from search for good; now they are
    // indexed again at the next start (0.1.4).
    long _applied;
    readonly object _commitLock = new();

    /// <summary>Saves both indexes to disk, then forgets the changes they now hold.</summary>
    void CommitIndex()
    {
        lock (_commitLock)
        {
            _index.Commit();
            if (_applied > 0) _cat.ClearDirty(_applied);
        }
    }

    async Task IndexLoop(CancellationToken stop)
    {
        var lastCommit = Stopwatch.StartNew();
        var lastRefresh = Stopwatch.StartNew();
        long sinceCommit = 0;
        bool unseen = false;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var (max, ids) = _cat.NextDirty(1000, _applied);
                if (ids.Count == 0)
                {
                    if (unseen) { _index.Refresh(); _index.Warm(); unseen = false; }
                    if (sinceCommit > 0) { CommitIndex(); sinceCommit = 0; lastCommit.Restart(); }
                    if (IndexBuilding) { IndexBuilding = false; _index.RefreshBlocking(); _index.Warm(); _log.Info("Search index is up to date."); }
                    Stats.Activity.TryRemove("index", out _);
                    _rules.CheckLookups();
                    await Task.Delay(1000, stop);
                    continue;
                }
                Stats.Activity["index"] = $"Indexing {ids.Count:N0} items";
                IndexBatch(ids);
                _applied = max;
                sinceCommit += ids.Count;
                // Show new files to searches every 20 s, not every batch: each new reader rebuilds the facet
                // tables, which slowed searches right down during big index builds. Warm them here, off the query path.
                unseen = true;
                if (lastRefresh.Elapsed > TimeSpan.FromSeconds(20)) { _index.Refresh(); _index.Warm(); lastRefresh.Restart(); unseen = false; }
                if (sinceCommit > 20000 || lastCommit.Elapsed > TimeSpan.FromSeconds(30)) { CommitIndex(); sinceCommit = 0; lastCommit.Restart(); }
            }
        }
        finally
        {
            // A clean stop saves what is in the writers, so the next start has nothing to redo.
            if (sinceCommit > 0) try { CommitIndex(); } catch (Exception e) { _log.Warn("index commit on stop: " + e.Message); }
        }
    }

    readonly ConcurrentDictionary<(string, string), List<EnrichOutput>> _enrichCache = new();

    public void IndexBatch(List<long> ids)
    {
        var rows = _cat.Load(ids);
        var declared = _plugins.DeclaredFields();
        _enrichCache.Clear();
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, id =>
        {
            if (!rows.TryGetValue(id, out var row)) { _index.Delete(id); return; }
            try
            {
                var pages = _cat.Pages(id);
                ApplyEnrichment(row);
                var d = _rules.Apply(row, pages);
                _index.Put(new SearchIndex.Input(row, d, pages, declared));
                Interlocked.Increment(ref Stats.Indexed);
            }
            catch (Exception e) { _log.Warn($"index {row.Name}: {e.Message}"); }
        });
    }

    void ApplyEnrichment(ItemRow row)
    {
        if (row.Collection is not { } coll) return;
        var list = _enrichCache.GetOrAdd((row.Dataset, coll), k => _cat.Enrichments(k.Item1, k.Item2));
        foreach (var e in list)
        {
            if (e.Geo is { Lat: not null } g) row.Geo = new Geo { Lat = g.Lat, Lon = g.Lon, Bbox = g.Bbox, Basis = g.Basis, Raw = row.Geo?.Raw };
            foreach (var (k, v) in e.Fields) foreach (var x in v) row.Meta.Add(k, x);
        }
    }

    // ---------- stats ----------

    async Task StatsLoop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested) { Stats.Sample(); await Task.Delay(10_000, stop); }
    }

    public override void Dispose()
    {
        foreach (var w in _watchers.Values) w.Dispose();
        _ocr?.Dispose();
        base.Dispose();
    }
}
