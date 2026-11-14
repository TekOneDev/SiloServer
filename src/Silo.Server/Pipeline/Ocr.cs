using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Silo.Server.Core;

namespace Silo.Server.Pipeline;

public sealed class OcrJob
{
    public string Path { get; set; } = "";
    public bool Image { get; set; }
    public List<int> Pages { get; set; } = new();
}

public sealed class OcrReply
{
    public Dictionary<int, string> Pages { get; set; } = new();
    public int Rotated { get; set; }
    public string? Error { get; set; }
    /// <summary>Set on a new worker's first reply: the sandbox steps it applied.</summary>
    public string? Isolation { get; set; }
}

/// <summary>
/// OCR, part of the core: Windows' own engine (Windows.Media.Ocr), no extra install. Runs in worker
/// processes of this executable ("SiloServer.exe ocr-worker") so a bad scan can only take down a worker.
/// Pages that read as nonsense are tried again turned 90° and 270° (sideways scans), keeping the best.
/// </summary>
public static class OcrEngine
{
#if WINOCR
    static Windows.Media.Ocr.OcrEngine? _engine;
    static bool _tried;
    static string? _why;

    static Windows.Media.Ocr.OcrEngine? Engine()
    {
        if (_tried) return _engine;
        _tried = true;
        try
        {
            _engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            foreach (var tag in new[] { "en-AU", "en-US", "en-GB" })
            {
                if (_engine != null) break;
                var lang = new Windows.Globalization.Language(tag);
                if (Windows.Media.Ocr.OcrEngine.IsLanguageSupported(lang)) _engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(lang);
            }
            if (_engine == null)
            {
                foreach (var l in Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages) { _engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(l); if (_engine != null) break; }
            }
            if (_engine == null) _why = "no Windows OCR language is installed (Settings > Time & language > Language > English > Optional features: Optical character recognition)";
        }
        catch (Exception e) { _why = e.Message; }
        return _engine;
    }

    public static string? Check() { Engine(); return _engine == null ? (_why ?? "unavailable") : null; }
    public static string Language => _engine?.RecognizerLanguage?.DisplayName ?? "";

    static string Recognize(Windows.Graphics.Imaging.BitmapDecoder decoder, uint frame, ref int rotated)
    {
        var engine = Engine()!;
        string best = ""; double bestScore = -1;
        foreach (var rot in new[] { Windows.Graphics.Imaging.BitmapRotation.None, Windows.Graphics.Imaging.BitmapRotation.Clockwise90Degrees, Windows.Graphics.Imaging.BitmapRotation.Clockwise270Degrees })
        {
            var fr = frame < decoder.FrameCount ? decoder.GetFrameAsync(frame).AsTask().GetAwaiter().GetResult() : decoder.GetFrameAsync(0).AsTask().GetAwaiter().GetResult();
            uint w = fr.PixelWidth, h = fr.PixelHeight;
            double scale = Math.Min(1.0, Windows.Media.Ocr.OcrEngine.MaxImageDimension / (double)Math.Max(w, h));
            var tf = new Windows.Graphics.Imaging.BitmapTransform { Rotation = rot, ScaledWidth = (uint)Math.Max(1, w * scale), ScaledHeight = (uint)Math.Max(1, h * scale), InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant };
            using var bmp = fr.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, tf,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
            var res = engine.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult();
            var text = string.Join('\n', res.Lines.Select(l => l.Text));
            var score = Quality(text);
            if (score > bestScore) { bestScore = score; best = text; if (rot != Windows.Graphics.Imaging.BitmapRotation.None) rotated++; }
            if (score >= 0.6 || text.Length < 30) break;   // reads well (or the page is nearly blank): no need to turn it
        }
        return best;
    }

    public static OcrReply Run(OcrJob job)
    {
        var reply = new OcrReply();
        if (Engine() == null) { reply.Error = _why; return reply; }
        int rotated = 0;
        if (job.Image)
        {
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(job.Path).AsTask().GetAwaiter().GetResult();
            using var stream = file.OpenAsync(Windows.Storage.FileAccessMode.Read).AsTask().GetAwaiter().GetResult();
            var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            uint frames = Math.Min(decoder.FrameCount, 500u);   // multi-page TIFF scans
            for (uint f = 0; f < frames; f++)
            {
                try { reply.Pages[(int)f + 1] = Recognize(decoder, f, ref rotated); } catch { reply.Pages[(int)f + 1] = ""; }
            }
        }
        else
        {
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(job.Path).AsTask().GetAwaiter().GetResult();
            var pdf = Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
            foreach (var n in job.Pages)
            {
                if (n < 1 || n > pdf.PageCount) continue;
                try
                {
                    using var page = pdf.GetPage((uint)(n - 1));
                    // About 2,400 px on the long side: roughly 200-300 dpi for A4/A3, inside the engine's limit.
                    double w = page.Size.Width, h = page.Size.Height;
                    double scale = Math.Min(2400 / Math.Max(1, Math.Max(w, h)), Windows.Media.Ocr.OcrEngine.MaxImageDimension / Math.Max(1, Math.Max(w, h)));
                    using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    var opts = new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, w * scale), DestinationHeight = (uint)Math.Max(1, h * scale), BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255) };
                    page.RenderToStreamAsync(stream, opts).AsTask().GetAwaiter().GetResult();
                    var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
                    reply.Pages[n] = Recognize(decoder, 0, ref rotated);
                }
                catch { reply.Pages[n] = ""; }
            }
        }
        reply.Rotated = rotated;
        return reply;
    }
#else
    public static string? Check() => "only available in the Windows build";
    public static string Language => "";
    public static OcrReply Run(OcrJob job) => new() { Error = "OCR is only available in the Windows build" };
#endif

    /// <summary>Share of tokens that look like real words (letters with a vowel, or numbers). Sideways scans score low.</summary>
    public static double Quality(string text)
    {
        var toks = text.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length == 0) return 0;
        int good = 0;
        foreach (var t in toks)
        {
            var w = t.Trim('.', ',', ';', ':', '(', ')', '"', '\'');
            if (w.Length == 0) continue;
            if (w.All(c => char.IsDigit(c) || c is '.' or ',' or '-' or '/' or '%')) { good++; continue; }
            if (w.Length >= 2 && w.All(char.IsLetter) && w.Any(c => "aeiouyAEIOUY".Contains(c))) good++;
        }
        return (double)good / toks.Length;
    }

    /// <summary>Worker process main: one JSON job per line in, one JSON reply per line out.</summary>
    public static int WorkerMain()
    {
        // Job, no privileges, and low integrity when settings.ocrLowIntegrity is on (0.1.5). Reported on the first reply.
        string? isolation;
        try { isolation = Silo.Plugins.Sandbox.ApplyIfAsked(); } catch (Exception e) { isolation = "FAILED (" + e.Message + ")"; }
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(Console.Error);
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            OcrReply r;
            try { r = Run(JsonSerializer.Deserialize<OcrJob>(line, Silo.Plugins.Contract.Json)!); }
            catch (Exception e) { r = new OcrReply { Error = e.GetType().Name + ": " + e.Message }; }
            r.Isolation = isolation; isolation = null;
            stdout.WriteLine(JsonSerializer.Serialize(r, Silo.Plugins.Contract.Json));
        }
        return 0;
    }
}

/// <summary>A pool of OCR worker processes.</summary>
public sealed class OcrPool : IDisposable
{
    readonly ConcurrentBag<Process> _idle = new();
    readonly SemaphoreSlim _slots;
    readonly List<Process> _all = new();
    readonly Log _log;
    public int Size { get; }

    readonly int _memoryMB;
    readonly bool _low;
    readonly string? _scratch;
    /// <summary>What the newest worker reported about its sandbox, for the admin page.</summary>
    public string Isolation { get; private set; } = "not started yet";

    public OcrPool(int size, Log log, int memoryMB = 4096, bool lowIntegrity = false, string? scratch = null)
    {
        Size = size; _slots = new SemaphoreSlim(size, size); _log = log; _memoryMB = memoryMB; _low = lowIntegrity; _scratch = scratch;
    }

    Process Start()
    {
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe, "ocr-worker")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.Environment[Silo.Plugins.Sandbox.EnvMode] = _low ? "1" : "job";
        psi.Environment[Silo.Plugins.Sandbox.EnvMemory] = _memoryMB.ToString();
        if (_low && _scratch != null) psi.Environment["TEMP"] = psi.Environment["TMP"] = _scratch;
        var p = Process.Start(psi)!;
        var problem = Silo.Plugins.Sandbox.Contain(p, (long)_memoryMB * 1024 * 1024);
        if (problem != null) _log.Warn("OCR worker job could not be set up: " + problem);
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        return p;
    }

    public OcrReply Run(OcrJob job, TimeSpan timeout, CancellationToken ct)
    {
        _slots.Wait(ct);
        Process? p = null;
        try
        {
            if (!_idle.TryTake(out p) || p.HasExited) { p = Start(); lock (_all) _all.Add(p); }
            p.StandardInput.WriteLine(JsonSerializer.Serialize(job, Silo.Plugins.Contract.Json));
            p.StandardInput.Flush();
            var read = p.StandardOutput.ReadLineAsync(ct).AsTask();
            if (!read.Wait(timeout, ct)) { Kill(p); p = null; return new OcrReply { Error = $"OCR timed out after {timeout.TotalSeconds:N0}s" }; }
            if (read.Result == null) { Kill(p); p = null; return new OcrReply { Error = "OCR worker crashed (damaged file?)" }; }
            var reply = JsonSerializer.Deserialize<OcrReply>(read.Result, Silo.Plugins.Contract.Json) ?? new OcrReply { Error = "bad reply" };
            if (reply.Isolation != null && reply.Isolation != Isolation)
            {
                if (reply.Isolation.Contains("FAILED")) _log.Warn("OCR worker sandbox: " + reply.Isolation);
                Isolation = reply.Isolation;
            }
            return reply;
        }
        catch (OperationCanceledException) { if (p != null) Kill(p); p = null; throw; }
        catch (Exception e) { if (p != null) Kill(p); p = null; return new OcrReply { Error = e.Message }; }
        finally { if (p != null) _idle.Add(p); _slots.Release(); }
    }

    static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } try { p.Dispose(); } catch { } }

    public void Dispose() { lock (_all) foreach (var p in _all) { try { p.StandardInput.Close(); } catch { } Kill(p); } }

    static readonly Regex Space = new(@"[ \t]+", RegexOptions.Compiled);
    public static string Clean(string s) => Space.Replace(Regex.Replace(s, @"(\p{L})-\n(\p{Ll})", "$1$2"), " ").Trim();
}
