using System.Text.RegularExpressions;
using Silo.Errors;
using Silo.Server.Api;
using Silo.Server.Core;
using Xunit;

namespace Silo.Tests;

/// <summary>
/// Keeps Silo Server on the error registry (SiloCommon, Silo.Errors). The scans read the source tree, so a new
/// user-facing error that skips the registry fails the build rather than reaching a user without a code.
/// </summary>
public class ErrorUseTests
{
    static readonly string Root = FindRoot();

    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SiloServer.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("The SiloServer source tree was not found above " + AppContext.BaseDirectory);
    }

    static IEnumerable<string> Sources(params string[] patterns) =>
        new[] { "src", "installer" }.Select(d => Path.Combine(Root, d)).Where(Directory.Exists)
            .SelectMany(d => patterns.SelectMany(p => Directory.EnumerateFiles(d, p, SearchOption.AllDirectories)))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                && !f.Contains(Path.Combine("wwwroot", "lib")) && !f.EndsWith(".min.js", StringComparison.Ordinal));

    static string Rel(string f) => Path.GetRelativePath(Root, f).Replace('\\', '/');

    [Fact]
    public void Every_code_written_in_the_source_is_in_the_registry()
    {
        var unknown = new List<string>();
        foreach (var f in Sources("*.cs", "*.js", "*.ps1", "*.html", "*.cmd"))
        {
            if (f.EndsWith("silo-errors.js", StringComparison.Ordinal)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"SILO-[A-Z]{3}-[A-Z]+-\d{4}"))
                if (ErrorRegistry.Find(m.Value) == null) unknown.Add($"{Rel(f)}: {m.Value}");
        }
        Assert.Empty(unknown);
    }


    [Fact]
    public void Every_code_in_the_registry_is_raised_somewhere()
    {
        // A code nobody raises has an article that describes something a person can never see. Retire it or wire it in.
        var dirs = new List<string> { Path.Combine(Root, "src"), Path.Combine(Root, "installer") };
        var text = new System.Text.StringBuilder();
        foreach (var d in dirs.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                if (f.EndsWith(".g.cs", StringComparison.Ordinal) || f.EndsWith("silo-errors.js", StringComparison.Ordinal) || f.Contains(Path.DirectorySeparatorChar + "registry" + Path.DirectorySeparatorChar)) continue;
                if (Path.GetExtension(f) is ".cs" or ".js" or ".ps1" or ".html" or ".cmd") text.Append(File.ReadAllText(f)).Append('\n');
            }
        var source = text.ToString();
        var unused = new List<string>();
        foreach (var d in ErrorRegistry.All.Where(d => !d.Deprecated && (d.Code.StartsWith("SILO-SRV-", StringComparison.Ordinal))))
        {
            var parts = d.Code.Split('-');
            string Title(string x) => x[..1] + x[1..].ToLowerInvariant();
            if (!source.Contains(d.Code, StringComparison.Ordinal) && !source.Contains($"{Title(parts[1])}.{Title(parts[2])}.{d.Name}", StringComparison.Ordinal)
                && !Regex.IsMatch(source, $@"(?<![\w.]){Title(parts[2])}\.{d.Name}\b"))
                unused.Add($"{d.Code} {d.Name}");
        }
        Assert.Empty(unused);
    }

    [Fact]
    public void The_pages_error_table_matches_the_registry()
    {
        // silo-errors.js is generated (tools/ErrorGen --js SRV); a registry change without regenerating it would show stale text.
        var js = File.ReadAllText(Path.Combine(Root, "src", "Silo.Server", "wwwroot", "js", "silo-errors.js")).ReplaceLineEndings("\n");
        Assert.Equal(Generation.Js(ErrorRegistry.All.OrderBy(d => d.Code, StringComparer.Ordinal).ToList(), "SRV").ReplaceLineEndings("\n"), js);
    }

    /// <summary>Ways an error used to reach a person as loose text. Each must now go through the registry.</summary>
    static readonly (string What, Regex Rx, string Files)[] Loose =
    [
        ("an API answer with loose error text (use Fail.Result)", new Regex(@"new\s*\{\s*error\s*=\s*\$?""", RegexOptions.Compiled), "*.cs"),
        ("an API answer with loose text (use Fail.Result)", new Regex(@"Results\.(NotFound|BadRequest|Conflict|Problem|Unauthorized)\(\s*(\$?""|new\s*\{\s*(error|message)\b)", RegexOptions.Compiled), "*.cs"),
        ("a log warning or error with loose text (use Log.Problem)", new Regex(@"\b_?log\.(Warn|Error)\(", RegexOptions.Compiled | RegexOptions.IgnoreCase), "*.cs"),
        ("a page error built from loose text (use siloError or errorFrom)", new Regex(@"new Error\(\s*['""`]", RegexOptions.Compiled), "*.js"),
        ("a page error built from the HTTP status text (use errorFrom)", new Regex(@"\.statusText", RegexOptions.Compiled), "*.js"),
        ("an installer failure without a code", new Regex(@"\bthrow\s+(""(?![^""\n]*SILO-[A-Z]{3}-[A-Z]+-\d{4})|')", RegexOptions.Compiled), "*.ps1"),
    ];

    [Fact]
    public void No_user_facing_error_is_raised_without_a_registry_code()
    {
        var found = new List<string>();
        foreach (var (what, rx, files) in Loose)
            foreach (var f in Sources(files))
            {
                var rel = Rel(f);
                if (rel.EndsWith("silo-errors.js", StringComparison.Ordinal)) continue;
                var n = 0;
                foreach (var line in File.ReadLines(f))
                {
                    n++;
                    var code = line.TrimStart();
                    if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith('#')) continue;
                    if (rx.IsMatch(line)) found.Add($"{rel}:{n}: {what}");
                }
            }
        Assert.Empty(found);
    }

    [Fact]
    public void The_page_calls_the_api_in_one_place()
    {
        // Every request goes through api(), which turns a failure into a coded error. The one other fetch loads a map outline shipped with the pages.
        var js = File.ReadAllText(Path.Combine(Root, "src", "Silo.Server", "wwwroot", "js", "app.js"));
        var calls = Regex.Matches(js, @"(?<![\w.])fetch\(([^)]*)").Select(m => m.Groups[1].Value.Trim()).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, c => c.StartsWith("\"/api/v1\" + path", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.StartsWith("\"/lib/geo/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_folder_that_cannot_be_read_says_which_way()
    {
        var missing = AdminRoutes.CannotReadFault(Path.Combine(Path.GetTempPath(), "silo-no-such-" + Guid.NewGuid().ToString("N")));
        Assert.Equal("SILO-SRV-SRC-0001", missing!.Value.Def.Code);
        Assert.EndsWith("(SILO-SRV-SRC-0001)", AdminRoutes.CannotRead(Path.Combine(Path.GetTempPath(), "silo-no-such-" + Guid.NewGuid().ToString("N"))));
        Assert.Null(AdminRoutes.CannotReadFault(Path.GetTempPath()));
    }

    [Fact]
    public void What_a_plugin_reports_gets_the_code_for_that_failure()
    {
        // Plugins answer "ExceptionType: message" (PluginRunner). The file keeps the code and the reader's own words as detail.
        var damaged = PluginFaults.OfExtract(new PluginError("InvalidDataException: Not a zip archive"));
        Assert.Contains("(SILO-SHR-READ-0004)", damaged);
        Assert.Contains("Detail: Not a zip archive", damaged);
        Assert.Contains("(SILO-SHR-READ-0008)", PluginFaults.OfExtract(new PluginError("InvalidOperationException: The document is encrypted")));
        Assert.Contains("(SILO-SHR-READ-0010)", PluginFaults.OfExtract(new PluginError("something odd")));
        var slow = PluginFaults.OfExtract(new SiloException(Err.Srv.Plug.PluginTookTooLong, new { plugin = "silo.filter.pdf", seconds = "120" }));
        Assert.Contains("(SILO-SRV-PLUG-0005)", slow);
        Assert.Contains("120", slow);
        Assert.Contains("(SILO-SRV-PLUG-0006)", PluginFaults.OfExtract(new SiloException(Err.Srv.Plug.PluginStopped, new { plugin = "silo.filter.pdf", exit = "-1" })));
    }

    [Fact]
    public void Every_reason_the_folder_source_gives_has_its_own_code()
    {
        // The folder plugin says why in words; a new reason nobody mapped would fall through to "could not be scanned" unnoticed.
        var src = File.ReadAllText(Path.Combine(Root, "src", "Silo.Plugin.Files", "FolderSource.cs"));
        var reasons = Regex.Matches(src, @"throw new (\w+)\(\$?""((?:[^""\\]|\\.)*)""").Select(m => m.Groups[1].Value + ": " + Regex.Replace(m.Groups[2].Value, @"\{[^}]*\}", "x")).ToList();
        Assert.NotEmpty(reasons);
        foreach (var r in reasons)
            Assert.NotEqual(Err.Srv.Src.ScanFailed.Code, PluginFaults.OfScan(new PluginError(r), "Library").Def.Code);
        Assert.Equal("SILO-SRV-SRC-0005", PluginFaults.OfScan(new PluginError("IOException: The network name was deleted"), "Library").Def.Code);
    }

    [Fact]
    public void Unreadable_settings_with_no_backup_stop_the_service_with_a_code()
    {
        var dir = Path.Combine(Path.GetTempPath(), "silo-errtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"listen\": [ ");
            var e = Assert.Throws<SiloException>(() => new SettingsStore(dir));
            Assert.Equal("SILO-SRV-CFG-0002", e.Code);
            Assert.DoesNotContain(dir, e.UserMessage);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unreadable_settings_with_a_backup_carry_on_and_say_so()
    {
        var dir = Path.Combine(Path.GetTempPath(), "silo-errtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var good = new SettingsStore(dir);
            good.Update(s => s.Ocr = !s.Ocr);   // saving keeps the previous file as settings.json.bak
            Assert.True(File.Exists(Path.Combine(dir, "settings.json.bak")));
            File.WriteAllText(Path.Combine(dir, "settings.json"), "not json");
            var again = new SettingsStore(dir);
            Assert.Equal("SILO-SRV-CFG-0001", again.LoadFault!.Value.Def.Code);
            Assert.EndsWith("(SILO-SRV-CFG-0001)", again.LoadProblem);
        }
        finally { Directory.Delete(dir, true); }
    }
}
