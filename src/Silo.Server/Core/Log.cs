using Silo.Errors;
using System.Collections.Concurrent;

namespace Silo.Server.Core;

public sealed record LogEntry(DateTime Time, string Level, string Message);

/// <summary>Service log: a ring buffer for the admin page, plus a daily file in DataDir\logs.</summary>
public sealed class Log
{
    readonly ConcurrentQueue<LogEntry> _recent = new();
    readonly string _dir;
    readonly object _fileLock = new();
    public bool Verbose { get; set; }

    public Log(string dataDir)
    {
        _dir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(_dir);
        // Keep two weeks of logs.
        try { foreach (var f in new DirectoryInfo(_dir).GetFiles("*.log").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14))) f.Delete(); } catch { }
    }

    public IEnumerable<LogEntry> Recent => _recent.Reverse();

    /// <summary>
    /// For a failure before the log exists (the settings that say where the log is could not be read): one line in the
    /// default log folder, so the reason is not lost when the service is started by Windows with no console.
    /// </summary>
    public static void Emergency(ErrorDef def, object? args = null, Exception? e = null)
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("SILO_HOME") ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SiloServer")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".siloserver"));
            var dir = Path.Combine(root, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"silo-{DateTime.Now:yyyy-MM-dd}.log"),
                $"{DateTime.Now:HH:mm:ss} error {new SiloError(def, args).Display} | {SiloError.LogLine(def, args, e)}{Environment.NewLine}");
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
    }

    void Write(string level, string msg)
    {
        var e = new LogEntry(DateTime.Now, level, msg);
        _recent.Enqueue(e);
        while (_recent.Count > 500) _recent.TryDequeue(out _);
        try { lock (_fileLock) File.AppendAllText(Path.Combine(_dir, $"silo-{DateTime.Now:yyyy-MM-dd}.log"), $"{e.Time:HH:mm:ss} {level,-5} {msg}{Environment.NewLine}"); } catch { }
        if (Environment.UserInteractive) Console.WriteLine($"{e.Time:HH:mm:ss} {level,-5} {msg}");
    }

    /// <summary>
    /// Writes a registry error with its full detail (code, the values behind the message, the exception) and returns
    /// the line to show a person: "message (CODE)".
    /// </summary>
    public string Problem(ErrorDef def, object? args = null, Exception? e = null, string? traceId = null)
    {
        var shown = new SiloError(def, args).Display;
        Write(def.Severity switch { Severity.Info => "info", Severity.Warning => "warn", _ => "error" },
            shown + " | " + SiloError.LogLine(def, args, e, traceId) + (e?.StackTrace is { Length: > 0 } st && def.Severity >= Severity.Error ? Environment.NewLine + st : ""));
        return shown;
    }

    public void Info(string m) => Write("info", m);
    public void Warn(string m) => Write("warn", m);
    public void Error(string m) => Write("error", m);
    public void Debug(string m) { if (Verbose) Write("debug", m); }
}
