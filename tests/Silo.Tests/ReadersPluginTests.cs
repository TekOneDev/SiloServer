using System.IO.Compression;
using Silo.Plugins;
using Silo.Plugins.Readers;
using Xunit;

namespace Silo.Tests;

/// <summary>0.1.6: the file types Silo Desktop reads, through the shared readers (SiloCommon).</summary>
public class ReadersPluginTests
{
    static string Temp(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "silo-rd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return Path.Combine(d, name);
    }

    static ExtractResult Run(string path) =>
        new ReadersFilter().Extract(new ExtractArgs { Path = path, Ext = Path.GetExtension(path), Size = new FileInfo(path).Length }, CancellationToken.None);

    [Fact]
    public void Claims_the_new_types_but_not_the_ones_older_plugins_own()
    {
        var exts = new ReadersFilter().Manifest.Extensions;
        foreach (var e in new[] { ".doc", ".xls", ".ppt", ".msg", ".eml", ".zip", ".odt", ".dwg", ".dxf", ".dgn", ".step", ".stl", ".dmx", ".aproj", ".slmd", ".fez" })
            Assert.Contains(e, exts);
        Assert.DoesNotContain(".dm", exts);
        Assert.DoesNotContain(".rs3v3", exts);
        Assert.DoesNotContain(".pdf", exts);
        Assert.DoesNotContain(".docx", exts);
    }

    [Fact]
    public void Reads_a_saved_mail()
    {
        var p = Temp("note.eml");
        File.WriteAllText(p, "From: Ann Geo <ann@example.com>\r\nTo: Bob <bob@example.com>\r\nSubject: Pit 3 slope review\r\nDate: Mon, 9 Nov 2026 10:00:00 +0800\r\n" +
                             "Content-Type: text/plain\r\n\r\nThe east wall factor of safety is 1.19 after the cutback.\r\n");
        var r = Run(p);
        var text = string.Join("\n", r.Pages.Select(x => x.Text));
        Assert.Contains("east wall factor of safety", text);
        Assert.Equal(1, r.Pages[0].N);
    }

    [Fact]
    public void Lists_what_is_in_a_zip()
    {
        var p = Temp("drilling.zip");
        using (var z = ZipFile.Open(p, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(z.CreateEntry("collars/RC-0123_collar.csv").Open());
            w.Write("hole,east,north\nRC-0123,1,2\n");
        }
        var text = string.Join("\n", Run(p).Pages.Select(x => x.Text));
        Assert.Contains("RC-0123_collar.csv", text);
    }

    [Fact]
    public void Reads_a_mesh_as_a_model_with_facts()
    {
        var p = Temp("bracket.stl");
        File.WriteAllText(p, "solid bracket\n facet normal 0 0 1\n  outer loop\n   vertex 0 0 0\n   vertex 10 0 0\n   vertex 0 10 0\n  endloop\n endfacet\n" +
                             " facet normal 0 0 1\n  outer loop\n   vertex 10 0 0\n   vertex 10 10 0\n   vertex 0 10 0\n  endloop\n endfacet\nendsolid bracket\n");
        var r = Run(p);
        Assert.True(r.Pages.Count > 0);
        Assert.False(string.IsNullOrWhiteSpace(r.Summary));
        Assert.True(r.Fields.ContainsKey("model_kind"));
    }
}
