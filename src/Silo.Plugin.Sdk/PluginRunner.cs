using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Silo.Plugins;

/// <summary>Base for a filter plugin: turn one file into text and fields.</summary>
public abstract class FilterPlugin
{
    public abstract PluginManifest Manifest { get; }
    public abstract ExtractResult Extract(ExtractArgs args, CancellationToken ct);
}

/// <summary>Base for a source plugin: list items, optionally watch for changes.</summary>
public abstract class SourcePlugin
{
    public abstract PluginManifest Manifest { get; }
    public abstract EnumerateResult Enumerate(EnumerateArgs args, Action<SourceItem> emit, CancellationToken ct);
    /// <summary>Start watching; call <paramref name="changed"/> with item keys (or "*" for "rescan everything").</summary>
    public virtual bool Watch(EnumerateArgs args, Action<IReadOnlyList<string>> changed) => false;
    /// <summary>Make a readable local copy of an item that has no path. Returns a path.</summary>
    public virtual string? Fetch(EnumerateArgs args, string key) => null;
    /// <summary>Re-read one item (after a change event). Null when it is gone.</summary>
    public virtual SourceItem? Get(EnumerateArgs args, string key) => null;
}

/// <summary>Base for an enricher plugin: add metadata to collections (see <see cref="EnrichArgs"/>).</summary>
public abstract class EnricherPlugin
{
    public abstract PluginManifest Manifest { get; }
    /// <summary>Datasets this enricher knows (matched against the "dataset" metadata key), or "*".</summary>
    public abstract EnrichResult Enrich(EnrichArgs args, CancellationToken ct);
}

/// <summary>
/// Runs a plugin over standard input/output (contract silo.plugin/1). A plugin's Main is one line:
/// <c>return PluginRunner.Run(new MyFilter());</c>
/// </summary>
public static class PluginRunner
{
    public static int Run(FilterPlugin plugin) => Loop(plugin.Manifest, (req, w) =>
    {
        switch (req.Op)
        {
            case "extract":
                var a = req.Args.Deserialize<ExtractArgs>(Contract.Json) ?? new ExtractArgs();
                var r = plugin.Extract(a, CancellationToken.None);
                Trim(r, a);
                w.Ok(req.Id, JsonSerializer.SerializeToNode(r, Contract.Json));
                return true;
        }
        return false;
    });

    public static int Run(EnricherPlugin plugin) => Loop(plugin.Manifest, (req, w) =>
    {
        if (req.Op != "enrich") return false;
        var a = req.Args.Deserialize<EnrichArgs>(Contract.Json) ?? new EnrichArgs();
        w.Ok(req.Id, JsonSerializer.SerializeToNode(plugin.Enrich(a, CancellationToken.None), Contract.Json));
        return true;
    });

    public static int Run(SourcePlugin plugin) => Loop(plugin.Manifest, (req, w) =>
    {
        var a = req.Args?["source"] is JsonObject so ? so.Deserialize<EnumerateArgs>(Contract.Json)! : req.Args.Deserialize<EnumerateArgs>(Contract.Json) ?? new EnumerateArgs();
        switch (req.Op)
        {
            case "enumerate":
                var res = plugin.Enumerate(a, item => w.Item(req.Id, item), CancellationToken.None);
                w.Ok(req.Id, JsonSerializer.SerializeToNode(res, Contract.Json));
                return true;
            case "watch":
                var ok = plugin.Watch(a, keys => w.Event("changed", keys));
                w.Ok(req.Id, new JsonObject { ["watching"] = ok });
                return true;
            case "get":
                var key = req.Args?["key"]?.GetValue<string>() ?? "";
                var item = plugin.Get(a, key);
                w.Ok(req.Id, item == null ? null : JsonSerializer.SerializeToNode(item, Contract.Json));
                return true;
            case "fetch":
                var p = plugin.Fetch(a, req.Args?["key"]?.GetValue<string>() ?? "");
                w.Ok(req.Id, new JsonObject { ["path"] = p });
                return true;
        }
        return false;
    });

    static void Trim(ExtractResult r, ExtractArgs a)
    {
        long chars = 0;
        for (int i = 0; i < r.Pages.Count; i++)
        {
            chars += r.Pages[i].Text.Length;
            if (chars > a.MaxChars || i >= a.MaxPages) { r.Warnings.Add($"text cut at page {i}"); r.Pages.RemoveRange(i, r.Pages.Count - i); break; }
        }
    }

    sealed class Writer
    {
        readonly Stream _out;
        readonly object _lock = new();
        public Writer(Stream s) { _out = s; }
        void Line(object o)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(o, Contract.Json);
            lock (_lock) { _out.Write(bytes); _out.WriteByte((byte)'\n'); _out.Flush(); }
        }
        public void Ok(long id, JsonNode? result) => Line(new Reply { Id = id, Ok = true, Result = result });
        public void Fail(long id, string error) => Line(new Reply { Id = id, Ok = false, Error = error });
        public void Item(long id, SourceItem item) => Line(new Reply { Id = id, Item = item });
        public void Event(string ev, IReadOnlyList<string> keys) => Line(new Reply { Event = ev, Keys = keys.ToList() });
        public void Log(string level, string msg) => Line(new Reply { Log = level, Msg = msg });
    }

    static int Loop(PluginManifest manifest, Func<Request, Writer, bool> handle)
    {
        // Keep stdout for the protocol only: anything a library prints goes to stderr instead.
        var stdout = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);
        var w = new Writer(stdout);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            Request? req;
            try { req = JsonSerializer.Deserialize<Request>(line, Contract.Json); }
            catch (Exception e) { w.Log("error", "bad request: " + e.Message); continue; }
            if (req == null) continue;
            try
            {
                switch (req.Op)
                {
                    case "hello": w.Ok(req.Id, JsonSerializer.SerializeToNode(manifest, Contract.Json)); continue;
                    case "shutdown": w.Ok(req.Id, null); return 0;
                    case "ping": w.Ok(req.Id, new JsonObject { ["pong"] = true }); continue;
                }
                if (!handle(req, w)) w.Fail(req.Id, $"unknown op '{req.Op}'");
            }
            catch (Exception e) { w.Fail(req.Id, e.GetType().Name + ": " + e.Message); }
        }
        return 0;
    }
}

/// <summary>Small helpers plugin authors tend to need.</summary>
public static class PluginUtil
{
    public static void Add(this Dictionary<string, List<string>> d, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!d.TryGetValue(key, out var l)) d[key] = l = new List<string>();
        value = value.Trim();
        if (!l.Contains(value, StringComparer.OrdinalIgnoreCase)) l.Add(value);
    }

    /// <summary>Printable ASCII/UTF-8 strings of at least <paramref name="min"/> characters in a byte buffer.</summary>
    public static IEnumerable<string> Strings(ReadOnlySpan<byte> buf, int min = 4)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        foreach (var b in buf)
        {
            if (b >= 32 && b < 127) sb.Append((char)b);
            else { if (sb.Length >= min) list.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length >= min) list.Add(sb.ToString());
        return list;
    }
}
