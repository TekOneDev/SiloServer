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

    void Write(string level, string msg)
    {
        var e = new LogEntry(DateTime.Now, level, msg);
        _recent.Enqueue(e);
        while (_recent.Count > 500) _recent.TryDequeue(out _);
        try { lock (_fileLock) File.AppendAllText(Path.Combine(_dir, $"silo-{DateTime.Now:yyyy-MM-dd}.log"), $"{e.Time:HH:mm:ss} {level,-5} {msg}{Environment.NewLine}"); } catch { }
        if (Environment.UserInteractive) Console.WriteLine($"{e.Time:HH:mm:ss} {level,-5} {msg}");
    }

    public void Info(string m) => Write("info", m);
    public void Warn(string m) => Write("warn", m);
    public void Error(string m) => Write("error", m);
    public void Debug(string m) { if (Verbose) Write("debug", m); }
}
