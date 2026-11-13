using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Silo.Plugins;

namespace Silo.Server.Core;

/// <summary>A plugin found on disk: its manifest and folder.</summary>
public sealed class PluginInfo
{
    public required PluginManifest Manifest { get; init; }
    public required string Dir { get; init; }
    public bool Enabled { get; set; } = true;
    public string? Problem { get; set; }
    public long Calls, Failures, Restarts, Timeouts;
    public double TotalMs;
    public string Id => Manifest.Id;
}

/// <summary>
/// One running copy of a plugin. Sends one request at a time over stdin and reads JSON lines from stdout.
/// A copy that times out, crashes or writes garbage is killed; the next call starts a fresh one.
/// </summary>
public sealed class PluginProcess : IDisposable
{
    readonly PluginInfo _info;
    readonly string _bin;
    readonly Log _log;
    Process? _p;
    long _next;
    readonly SemaphoreSlim _one = new(1, 1);
    readonly BlockingCollection<Reply> _replies = new();
    public Action<Reply>? OnEvent;

    public PluginProcess(PluginInfo info, string bin, Log log) { _info = info; _bin = bin; _log = log; }

    public bool Running => _p is { HasExited: false };

    void Start()
    {
        var m = _info.Manifest;
        var exe = m.Exec.Replace("{bin}", _bin.TrimEnd('\\', '/'));
        if (!Path.IsPathRooted(exe)) exe = Path.Combine(_info.Dir, exe);
        if (!OperatingSystem.IsWindows() && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(exe)) exe = exe[..^4];
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _info.Dir,
            StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in m.Args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.Debug($"[{m.Id}] {e.Data}"); };
        p.BeginErrorReadLine();
        _p = p;
        var replies = _replies;
        new Thread(() => ReadLoop(p, replies)) { IsBackground = true, Name = "plugin-" + m.Id }.Start();
        // Handshake: the contract's major version must match.
        var hello = SendRaw("hello", null, TimeSpan.FromSeconds(30), null);
        var got = hello?.Deserialize<PluginManifest>(Contract.Json);
        if (got == null || got.Contract.Split('/').FirstOrDefault() != "silo.plugin" || got.Contract != Contract.Version)
            throw new InvalidOperationException($"plugin {m.Id} speaks '{got?.Contract}', the core speaks '{Contract.Version}'");
    }

    void ReadLoop(Process p, BlockingCollection<Reply> replies)
    {
        try
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                Reply? r;
                try { r = JsonSerializer.Deserialize<Reply>(line, Contract.Json); }
                catch { _log.Warn($"[{_info.Id}] wrote something that is not JSON: {Trunc(line)}"); continue; }
                if (r == null) continue;
                if (r.Log != null) { _log.Info($"[{_info.Id}] {r.Msg}"); continue; }
                if (r.Event != null && r.Id == null) { try { OnEvent?.Invoke(r); } catch { } continue; }
                replies.Add(r);
            }
        }
        catch { }
        finally { try { replies.Add(new Reply { Id = -1, Ok = false, Error = "plugin exited" }); } catch { } }
    }

    static string Trunc(string s) => s.Length > 200 ? s[..200] + "…" : s;

    /// <summary>Sends a request and waits for the reply. Items streamed by a source go to <paramref name="onItem"/>.</summary>
    public JsonNode? Call(string op, JsonObject? args, TimeSpan timeout, Action<SourceItem>? onItem = null, CancellationToken ct = default)
    {
        _one.Wait(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            if (!Running) { if (_p != null) _info.Restarts++; Kill(); Start(); }
            Interlocked.Increment(ref _info.Calls);
            return SendRaw(op, args, timeout, onItem, ct);
        }
        catch (TimeoutException) { Interlocked.Increment(ref _info.Timeouts); Interlocked.Increment(ref _info.Failures); Kill(); throw; }
        catch (PluginError) { Interlocked.Increment(ref _info.Failures); throw; }
        catch (Exception) { Interlocked.Increment(ref _info.Failures); Kill(); throw; }
        finally { _info.TotalMs += sw.Elapsed.TotalMilliseconds; _one.Release(); }
    }

    JsonNode? SendRaw(string op, JsonObject? args, TimeSpan timeout, Action<SourceItem>? onItem, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _next);
        var req = JsonSerializer.Serialize(new Request { Id = id, Op = op, Args = args }, Contract.Json);
        _p!.StandardInput.WriteLine(req);
        _p.StandardInput.Flush();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException($"{_info.Id} took longer than {timeout.TotalSeconds:N0}s");
            if (!_replies.TryTake(out var r, (int)(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1)).TotalMilliseconds, ct))
            {
                if (!Running && _replies.Count == 0) throw new InvalidOperationException($"{_info.Id} stopped unexpectedly (exit code {SafeExit()})");
                continue;
            }
            if (r.Id == -1) throw new InvalidOperationException($"{_info.Id} stopped unexpectedly (exit code {SafeExit()})");
            if (r.Id != id) continue;   // a late reply to a request that already timed out
            if (r.Item != null) { onItem?.Invoke(r.Item); deadline = DateTime.UtcNow + timeout; continue; }
            if (r.Ok == true) return r.Result;
            throw new PluginError(r.Error ?? "failed");
        }
    }

    string SafeExit() { try { return _p?.HasExited == true ? _p.ExitCode.ToString() : "?"; } catch { return "?"; } }

    void Kill()
    {
        try { if (_p is { HasExited: false }) _p.Kill(true); } catch { }
        try { _p?.Dispose(); } catch { }
        _p = null;
        while (_replies.TryTake(out _)) { }
    }

    public void Dispose()
    {
        try
        {
            if (Running && _one.Wait(2000))
            {
                try { _p!.StandardInput.WriteLine(JsonSerializer.Serialize(new Request { Id = 0, Op = "shutdown" }, Contract.Json)); _p.StandardInput.Flush(); _p.WaitForExit(2000); }
                finally { _one.Release(); }
            }
        }
        catch { }
        Kill();
    }
}

public sealed class PluginError(string message) : Exception(message);

/// <summary>A pool of copies of one filter or enricher plugin, sized by its manifest and the core's limits.</summary>
public sealed class PluginPool : IDisposable
{
    readonly ConcurrentBag<PluginProcess> _idle = new();
    readonly SemaphoreSlim _slots;
    readonly Func<PluginProcess> _make;
    readonly List<PluginProcess> _all = new();
    public PluginInfo Info { get; }
    public int Size { get; }

    public PluginPool(PluginInfo info, int size, Func<PluginProcess> make)
    {
        Info = info; Size = size; _make = make;
        _slots = new SemaphoreSlim(size, size);
    }

    public JsonNode? Call(string op, JsonObject args, TimeSpan timeout, CancellationToken ct)
    {
        _slots.Wait(ct);
        PluginProcess? p = null;
        try
        {
            if (!_idle.TryTake(out p)) { p = _make(); lock (_all) _all.Add(p); }
            return p.Call(op, args, timeout, null, ct);
        }
        finally { if (p != null) _idle.Add(p); _slots.Release(); }
    }

    public void Dispose() { lock (_all) foreach (var p in _all) p.Dispose(); }
}

/// <summary>Finds plugins (built-in folder, then extra folders) and hands out pools and source processes.</summary>
public sealed class PluginManager : IDisposable
{
    readonly SettingsStore _settings;
    readonly Log _log;
    readonly ConcurrentDictionary<string, PluginPool> _pools = new();
    public List<PluginInfo> All { get; private set; } = new();

    public PluginManager(SettingsStore settings, Log log)
    {
        _settings = settings; _log = log;
        Discover();
    }

    public void Discover()
    {
        var builtIn = Path.Combine(_settings.BinDir, "plugins");
        var dirs = new List<string> { builtIn, Path.Combine(_settings.Root, "plugins") };
        dirs.AddRange(_settings.Current.PluginDirs);
        var found = new Dictionary<string, PluginInfo>();
        foreach (var root in dirs.Where(Directory.Exists))
            foreach (var mf in Directory.EnumerateFiles(root, "plugin.json", SearchOption.AllDirectories))
            {
                try
                {
                    // A plugin is a program the service runs. Outside the install folder it must have been put there by an administrator.
                    if (root != builtIn && !DataFolder.TrustedPlugin(Path.GetDirectoryName(mf)!, out var why))
                    {
                        _log.Warn($"Plugin in {Path.GetDirectoryName(mf)} not loaded: {why}. An administrator must copy it there.");
                        continue;
                    }
                    var m = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(mf), Contract.Json)!;
                    if (string.IsNullOrWhiteSpace(m.Id)) continue;
                    var info = new PluginInfo { Manifest = m, Dir = Path.GetDirectoryName(mf)! };
                    if (m.Contract != Contract.Version) info.Problem = $"needs contract {m.Contract}; this core speaks {Contract.Version}";
                    info.Enabled = info.Problem == null && !_settings.Current.DisabledPlugins.Contains(m.Id);
                    m.Extensions = m.Extensions.Select(e => e.ToLowerInvariant()).ToList();
                    found[m.Id] = info;   // later folders override built-ins with the same id
                }
                catch (Exception e) { _log.Warn($"Bad plugin manifest {mf}: {e.Message}"); }
            }
        All = found.Values.OrderBy(p => p.Manifest.Kind).ThenBy(p => p.Manifest.Name).ToList();
    }

    public PluginInfo? Get(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>The filter for a file: exact name first, then extension, then a catch-all; highest priority wins.</summary>
    public PluginInfo? FilterFor(string name, string? ext)
    {
        var filters = All.Where(p => p.Enabled && p.Manifest.Kind == "filter").ToList();
        var byName = filters.Where(p => p.Manifest.Names.Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase) && string.IsNullOrEmpty(ext))
            .OrderByDescending(p => p.Manifest.Priority).FirstOrDefault();
        if (byName != null) return byName;
        if (!string.IsNullOrEmpty(ext))
        {
            var byExt = filters.Where(p => p.Manifest.Extensions.Contains(ext)).OrderByDescending(p => p.Manifest.Priority).FirstOrDefault();
            if (byExt != null) return byExt;
        }
        return filters.Where(p => p.Manifest.Extensions.Contains("*")).OrderByDescending(p => p.Manifest.Priority).FirstOrDefault();
    }

    public IEnumerable<PluginInfo> Enrichers => All.Where(p => p.Enabled && p.Manifest.Kind == "enricher");

    public PluginPool Pool(PluginInfo info, int cap) =>
        _pools.GetOrAdd(info.Id, _ => new PluginPool(info, Math.Max(1, Math.Min(cap, info.Manifest.Concurrency)), () => new PluginProcess(info, _settings.BinDir, _log)));

    public PluginProcess Process(PluginInfo info) => new(info, _settings.BinDir, _log);

    /// <summary>Every field a plugin declares, by stored name ("x.{name}").</summary>
    public Dictionary<string, (FieldSpec spec, string plugin)> DeclaredFields()
    {
        var d = new Dictionary<string, (FieldSpec, string)>();
        foreach (var p in All.Where(p => p.Enabled))
            foreach (var f in p.Manifest.Fields)
                d.TryAdd("x." + f.Name, (f, p.Manifest.Name));
        return d;
    }

    public void Dispose() { foreach (var p in _pools.Values) p.Dispose(); }
}
