using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Silo.Plugins;

/// <summary>
/// The Silo plugin contract, version 1 ("silo.plugin/1").
///
/// A plugin is any executable. The core starts it, writes one JSON request per line to its standard input and
/// reads JSON lines from its standard output. Standard error is captured into the core's log.
///
///   request   {"id":7,"op":"extract","args":{...}}
///   item      {"id":7,"item":{...}}                (sources only: streamed while enumerating)
///   reply     {"id":7,"ok":true,"result":{...}}    or {"id":7,"ok":false,"error":"..."}
///   event     {"event":"changed","keys":[...]}     (sources only: unsolicited, after "watch")
///   log       {"log":"info","msg":"..."}           (any time)
///
/// Every plugin answers "hello" (returns its manifest) and "shutdown". Sources answer "enumerate" and may
/// answer "watch" and "fetch". Filters answer "extract". Unknown ops must reply ok:false, never crash.
/// The core only ever sends a plugin one request at a time; for parallelism it starts more copies.
/// Additive changes (new optional fields) keep the contract at /1; anything else bumps it.
/// </summary>
public static class Contract
{
    public const string Version = "silo.plugin/1";
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };
}

/// <summary>plugin.json: what a plugin is, how to start it and what it handles.</summary>
public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "0.0.0";
    public string Contract { get; set; } = Plugins.Contract.Version;
    /// <summary>"source" (finds items), "filter" (reads one file type into text and fields) or "enricher" (adds metadata to collections).</summary>
    public string Kind { get; set; } = "filter";
    public string? Description { get; set; }
    public string? Publisher { get; set; }
    /// <summary>Executable, relative to the plugin folder. "{bin}" is the core's own program folder.</summary>
    public string Exec { get; set; } = "";
    public List<string> Args { get; set; } = new();
    /// <summary>Filters: lower-case extensions with the dot, e.g. ".pdf". "*" matches any file nothing else claims.</summary>
    public List<string> Extensions { get; set; } = new();
    /// <summary>Enrichers: the datasets (metadata key "dataset") they handle.</summary>
    public List<string> Datasets { get; set; } = new();
    /// <summary>Filters: exact file names (no extension) this plugin reads, e.g. "engine". Case-insensitive.</summary>
    public List<string> Names { get; set; } = new();
    /// <summary>Filters: higher wins when two plugins claim one extension.</summary>
    public int Priority { get; set; }
    /// <summary>How many copies the core may run at once (capped by the core's own limit).</summary>
    public int Concurrency { get; set; } = 1;
    public int TimeoutSec { get; set; } = 300;
    /// <summary>Filters: files bigger than this get a metadata-only record (name, size, dates).</summary>
    public long MaxFileMB { get; set; } = 2048;
    /// <summary>Filters: the plugin wants only the head of huge files (it reads them itself, in place).</summary>
    public bool ReadsInPlace { get; set; }
    /// <summary>Fields this plugin may emit. Facet fields become filters in the search UI automatically.</summary>
    public List<FieldSpec> Fields { get; set; } = new();
    /// <summary>Sources: default settings shown in the admin page when a source is added.</summary>
    public JsonObject? ConfigTemplate { get; set; }
    /// <summary>
    /// False when the plugin cannot run sandboxed (it must write outside its scratch folder, or start other programs).
    /// The server then runs it with the service's full rights and says so on the admin page. Default true (0.1.5).
    /// </summary>
    public bool Sandbox { get; set; } = true;
    /// <summary>Filled in by the running plugin in its "hello" reply: which sandbox steps it applied to itself (0.1.5).</summary>
    public string? Isolation { get; set; }
}

public sealed class FieldSpec
{
    /// <summary>Lower-case, letters, digits and underscore. Plugin fields are stored as "x.{name}".</summary>
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>keyword (exact values), text (searchable words), number, date.</summary>
    public string Type { get; set; } = "keyword";
    public bool Facet { get; set; }
    public bool Searchable { get; set; } = true;
    /// <summary>Shown on result cards.</summary>
    public bool Show { get; set; }
}

// ---------------- messages ----------------

public sealed class Request
{
    public long Id { get; set; }
    public string Op { get; set; } = "";
    public JsonObject? Args { get; set; }
}

public sealed class Reply
{
    public long? Id { get; set; }
    public bool? Ok { get; set; }
    public JsonNode? Result { get; set; }
    public string? Error { get; set; }
    public SourceItem? Item { get; set; }
    public string? Event { get; set; }
    public List<string>? Keys { get; set; }
    public string? Log { get; set; }
    public string? Msg { get; set; }
}

// ---------------- sources ----------------

public sealed class EnumerateArgs
{
    public string SourceId { get; set; } = "";
    public JsonObject Config { get; set; } = new();
    /// <summary>Opaque value the plugin returned last time; null on the first run.</summary>
    public string? Checkpoint { get; set; }
}

public sealed class EnumerateResult
{
    public string? Checkpoint { get; set; }
    public long Count { get; set; }
    /// <summary>True when the plugin listed everything (so items not seen are gone); false for a delta.</summary>
    public bool Complete { get; set; } = true;
}

/// <summary>One thing a source found: a file, a mail, a record.</summary>
public sealed class SourceItem
{
    /// <summary>Stable key, unique within the source (e.g. the path relative to the source root).</summary>
    public string Key { get; set; } = "";
    /// <summary>Changes whenever the item or its metadata changes (e.g. size+mtime+sidecar mtime).</summary>
    public string Version { get; set; } = "";
    /// <summary>A path the core and filters can read; null when the text comes inline or via "fetch".</summary>
    public string? Path { get; set; }
    public string Name { get; set; } = "";
    public string? Ext { get; set; }
    public long Size { get; set; }
    public DateTime? Modified { get; set; }
    public DateTime? Created { get; set; }
    /// <summary>Where the item lives, for display and opening (folder path, URL).</summary>
    public string? Location { get; set; }
    public string? Url { get; set; }
    /// <summary>Who may see it: Windows SIDs ("S-1-5-..."), "group:{name}", or "public". Empty means nobody (fail closed).</summary>
    public List<string> Allow { get; set; } = new();
    /// <summary>Explicit denies, which win over allows.</summary>
    public List<string> Deny { get; set; } = new();
    /// <summary>Descriptive metadata. Well-known keys: title, collection, collection_title, company, commodity, year,
    /// report_type, tenement, licence, attribution, landing_page, author, abstract, lat, lon, bbox, state.</summary>
    public Dictionary<string, List<string>> Meta { get; set; } = new();
    /// <summary>Text that came with the item (mail bodies, records); filters are skipped when set.</summary>
    public string? Text { get; set; }
}

// ---------------- filters ----------------

public sealed class ExtractArgs
{
    public string Path { get; set; } = "";
    public string Ext { get; set; } = "";
    public long Size { get; set; }
    public int MaxPages { get; set; } = 5000;
    public long MaxChars { get; set; } = 30_000_000;
    public JsonObject? Options { get; set; }
}

public sealed class ExtractResult
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public DateTime? Created { get; set; }
    /// <summary>Text by page (or sheet, slide, section). Page numbers start at 1.</summary>
    public List<PageText> Pages { get; set; } = new();
    public int TotalPages { get; set; }
    /// <summary>Plugin fields (declared in the manifest) and well-known metadata keys.</summary>
    public Dictionary<string, List<string>> Fields { get; set; } = new();
    /// <summary>Entities found, e.g. ("hole","RC-0123"), ("material","Fresh_FW").</summary>
    public List<Entity> Entities { get; set; } = new();
    public Geo? Geo { get; set; }
    /// <summary>A short, readable summary for the result card (e.g. "Block model 120×80×60, 576,000 cells").</summary>
    public string? Summary { get; set; }
    /// <summary>For images: the core should OCR the whole file.</summary>
    public bool OcrImage { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed class PageText
{
    public int N { get; set; }
    public string Text { get; set; } = "";
    /// <summary>The page has no usable text layer; the core will OCR it.</summary>
    public bool NeedsOcr { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Ocr { get; set; }
}

public sealed record Entity(string Type, string Value);

public sealed class Geo
{
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    /// <summary>West, south, east, north in WGS84 degrees.</summary>
    public double[]? Bbox { get; set; }
    /// <summary>Where the location came from: metadata, exif, text, model-offset.</summary>
    public string? Basis { get; set; }
    /// <summary>Projected coordinates as found (e.g. "MGA94 z50 567936E 7019574N"), for display.</summary>
    public string? Raw { get; set; }
}

// ---------------- enrichers ----------------

/// <summary>
/// Enrichers add metadata to whole collections (a report and all its files) from somewhere else: a web
/// service, a project system, a spreadsheet. The core calls "enrich" in batches and caches what comes back
/// per (dataset, collection), so an enricher is asked about each collection once.
/// </summary>
public sealed class EnrichArgs
{
    public List<EnrichTarget> Targets { get; set; } = new();
    public JsonObject? Options { get; set; }
}

public sealed class EnrichTarget
{
    public string Dataset { get; set; } = "";
    public string Collection { get; set; } = "";
    public Dictionary<string, List<string>> Meta { get; set; } = new();
}

public sealed class EnrichResult
{
    public List<EnrichOutput> Items { get; set; } = new();
}

public sealed class EnrichOutput
{
    public string Dataset { get; set; } = "";
    public string Collection { get; set; } = "";
    /// <summary>False when the lookup failed for a reason worth retrying later (network); true for "nothing found".</summary>
    public bool Final { get; set; } = true;
    public Dictionary<string, List<string>> Fields { get; set; } = new();
    public Geo? Geo { get; set; }
    /// <summary>Footprint as GeoJSON geometry (WGS84), simplified; shown on the map.</summary>
    public JsonNode? Footprint { get; set; }
}
