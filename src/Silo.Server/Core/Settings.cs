using Silo.Errors;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Silo.Server.Core;

public sealed class SourceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Plugin { get; set; } = "silo.source.folder";
    public bool Enabled { get; set; } = true;
    public JsonObject Config { get; set; } = new();
    /// <summary>Minutes between full rescans (changes in between come from the plugin's watcher).</summary>
    public int RescanMinutes { get; set; } = 360;
}

public sealed class CsvLookup
{
    /// <summary>CSV file; reloaded when it changes. "{root:source-id}" expands to a source's root folder.</summary>
    public string Path { get; set; } = "";
    /// <summary>Column matched against the item's "dataset" metadata, and the one matched against "collection".</summary>
    public string DatasetColumn { get; set; } = "source";
    public string CollectionColumn { get; set; } = "item_id";
    /// <summary>CSV column → field name. Multiple rows per collection add multiple values.</summary>
    public Dictionary<string, string> Fields { get; set; } = new();
}

public sealed class ServerSettings
{
    /// <summary>Where the catalog and index live. Put it on the fastest disk (NVMe), not on the data shelf.</summary>
    public string? DataDir { get; set; }
    public List<string> Listen { get; set; } = ["http://localhost:47820"];
    /// <summary>anonymous: everyone sees only items marked public or readable by Everyone. windows: Windows sign-in (Negotiate), results trimmed by the user's groups.</summary>
    public string Auth { get; set; } = "anonymous";
    public int ExtractWorkers { get; set; } = 0;     // 0 = cores - 2
    public int OcrWorkers { get; set; } = 0;         // 0 = cores / 2
    public bool Ocr { get; set; } = true;
    public int MaxOcrPagesPerFile { get; set; } = 400;
    public bool Enrich { get; set; } = true;
    public List<SourceConfig> Sources { get; set; } = new();
    public List<CsvLookup> Lookups { get; set; } = new();
    /// <summary>Extra plugin folders (each holds &lt;plugin&gt;\plugin.json).</summary>
    public List<string> PluginDirs { get; set; } = new();
    /// <summary>Plugins switched off by id.</summary>
    public List<string> DisabledPlugins { get; set; } = new();
    /// <summary>Run plugins in a sandbox: own job, no privileges, low integrity (0.1.5). A plugin can opt out in its manifest.</summary>
    public bool SandboxPlugins { get; set; } = true;
    /// <summary>Memory cap for each plugin and OCR worker process, in MB.</summary>
    public int PluginMemoryMB { get; set; } = 4096;
    /// <summary>Also run OCR workers at low integrity. Off by default until checked on a real server: Windows OCR opens files through WinRT.</summary>
    public bool OcrLowIntegrity { get; set; }
    /// <summary>
    /// HTTPS certificate for "https://" listen addresses (0.1.5): a thumbprint or subject name in the computer's
    /// certificate store (LocalMachine\My), or a .pfx file path. Empty: the newest valid certificate named "Silo Server".
    /// </summary>
    public string? Certificate { get; set; }
    /// <summary>When HTTPS is on, send plain-HTTP requests from other computers to the HTTPS address (this computer keeps HTTP).</summary>
    public bool HttpsRedirect { get; set; } = true;
    /// <summary>Optional hook for search by meaning (parked; see ISemanticProvider).</summary>
    public string? Semantic { get; set; }
}

/// <summary>Settings in DataDir\settings.json, edited by the admin page or by hand (restart to apply listen/auth).</summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true };
    readonly string _file;
    readonly object _lock = new();

    public string Root { get; }
    public string DataDir { get; }
    public string BinDir { get; } = AppContext.BaseDirectory;
    public ServerSettings Current { get; private set; }

    public SettingsStore(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("SILO_HOME") ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SiloServer")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".siloserver"));
        Directory.CreateDirectory(Root);
        _file = Path.Combine(Root, "settings.json");
        Current = File.Exists(_file) ? Load() : Defaults();
        DataDir = string.IsNullOrWhiteSpace(Current.DataDir) ? Root : Current.DataDir!;
        Directory.CreateDirectory(DataDir);
        if (!File.Exists(_file)) Save(Current);
    }

    /// <summary>Set when settings.json could not be read at start; Program writes it to the log.</summary>
    public string? LoadProblem => LoadFault is { } f ? new SiloError(f.Def, f.Args).Display : null;
    /// <summary>The same, as its registry code with the values and exception behind it.</summary>
    public (ErrorDef Def, object? Args, Exception? Cause)? LoadFault { get; private set; }

    /// <summary>
    /// Reads settings.json. A file that cannot be read (a typo from a hand edit, a half-written file) used to stop the
    /// service at every start. Now the unreadable file is kept beside it, and the last good copy is used (0.1.4).
    /// </summary>
    ServerSettings Load()
    {
        try { return JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(_file), Opts) ?? throw new JsonException("the file is empty"); }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            var kept = Path.Combine(Root, $"settings.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try { File.Copy(_file, kept, true); } catch { }
            var bak = _file + ".bak";
            try
            {
                var s = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(bak), Opts) ?? throw new JsonException("empty");
                LoadFault = (Err.Srv.Cfg.SettingsRestoredFromBackup, new { kept = Path.GetFileName(kept), detail = SafeText.Detail(e) }, e);
                File.Copy(bak, _file, true);
                return s;
            }
            catch (Exception e2) when (e2 is JsonException or IOException or NotSupportedException)
            {
                // No good copy: stopping is safer than starting with default sources and addresses.
                throw new SiloException(Err.Srv.Cfg.SettingsUnreadableNoBackup, new { kept = Path.GetFileName(kept), detail = SafeText.Detail(e) }, e);
            }
        }
    }

    static ServerSettings Defaults()
    {
        var s = new ServerSettings();
        // The public mining library from the open-geo-library downloader, if it is on this PC.
        var lib = OperatingSystem.IsWindows() ? @"D:\PublicMiningData" : "/data/PublicMiningData";
        s.Sources.Add(new SourceConfig
        {
            Id = "public-library",
            Name = "Public mining library",
            Plugin = "silo.source.folder",
            Config = new JsonObject
            {
                ["root"] = lib,
                ["access"] = "public",
                ["sidecars"] = true,
                ["exclude"] = new JsonArray("_state", "_reference", "*.part", "*.tmp", "~$*", "Thumbs.db", "desktop.ini", "library.csv"),
            },
        });
        s.Lookups.Add(new CsvLookup
        {
            Path = "{root:public-library}/_reference/companies/item-companies.csv",
            Fields = new() { ["asx_code"] = "asx_code", ["company_name"] = "company_listed", ["current_company_name"] = "company_now", ["status"] = "company_status" },
        });
        return s;
    }

    public void Save(ServerSettings s)
    {
        lock (_lock)
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
            // Keep the settings that were in use as the last good copy.
            try { if (File.Exists(_file) && LoadProblem == null) File.Copy(_file, _file + ".bak", true); } catch (IOException) { }
            File.Move(tmp, _file, true);
            Current = s;
        }
    }

    public void Update(Action<ServerSettings> change)
    {
        lock (_lock)
        {
            var copy = JsonSerializer.Deserialize<ServerSettings>(JsonSerializer.Serialize(Current, Opts), Opts)!;
            change(copy);
            Save(copy);
        }
    }

    public string? SourceRoot(string sourceId) =>
        Current.Sources.FirstOrDefault(s => s.Id == sourceId)?.Config["root"]?.GetValue<string>();

    public int ExtractWorkers => Current.ExtractWorkers > 0 ? Current.ExtractWorkers : Math.Max(2, Environment.ProcessorCount - 2);
    public int OcrWorkers => Current.OcrWorkers > 0 ? Current.OcrWorkers : Math.Max(1, Environment.ProcessorCount - 2);   // below-normal priority, so searches stay quick
}
