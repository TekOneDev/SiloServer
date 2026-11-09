using Silo.Plugins;
using Silo.Plugins.Datamine;
using Silo.Plugins.Office;
using Silo.Server.Core;
using Silo.Server.Index;
using Xunit;

namespace Silo.Tests;

public class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "silo-tests-" + Guid.NewGuid().ToString("N"));
    public Rules Rules { get; }
    public Fixture()
    {
        var settings = new SettingsStore(Root);
        Rules = new Rules(settings, new Log(settings.DataDir), null);
    }
    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

public class RulesTests(Fixture fx) : IClassFixture<Fixture>
{
    [Theory]
    [InlineData("E28/589", "E 28/589")]
    [InlineData("E 28/589", "E 28/589")]
    [InlineData("e28 / 0589", "E 28/589")]
    [InlineData("E8/1622", "E 08/1622")]
    [InlineData("E08/1622", "E 08/1622")]
    [InlineData("M15/549", "M 15/549")]
    [InlineData("EPM 17533", "EPM 17533")]
    [InlineData("EPM17533", "EPM 17533")]
    [InlineData("EPC 0839", "EPC 839")]
    public void Tenements_normalise_to_one_form(string typed, string expected)
        => Assert.Equal(expected, fx.Rules.NormaliseTenement(typed));

    [Theory]
    [InlineData("au", "Gold")]
    [InlineData("GOLD", "Gold")]
    [InlineData("cu", "Copper")]
    [InlineData("gaseous hydrocarbons", "Petroleum")]
    public void Commodity_aliases(string raw, string canon) => Assert.Equal(canon, fx.Rules.CanonCommodity(raw));

    [Fact]
    public void Query_operators_become_filters()
    {
        var q = QueryParser.Parse("gold drilling commodity:cu type:annual year:1990..2005 -laterite", fx.Rules);
        Assert.Equal(2, q.Clauses.Count);
        Assert.Contains("Copper", q.Filters[F.DCommodity]);
        Assert.Equal(1990, q.YearFrom);
        Assert.Equal(2005, q.YearTo);
        Assert.Contains("laterite", q.Excluded);
        Assert.Contains(q.Chips, c => c.Dim == F.DRType);
    }

    [Fact]
    public void Query_recognises_tenements_and_report_ids()
    {
        var q = QueryParser.Parse("E 8/1622 A59033", fx.Rules);
        Assert.Contains("E 08/1622", q.Tenements);
        Assert.Contains("A59033", q.ReportIds);
    }
}

public class AnalysisTests
{
    [Fact]
    public void Letters_and_digits_split_so_tenement_forms_match()
    {
        Assert.Equal(new[] { "e", "28", "589" }, SiloAnalyzer.Tokens("E28/589"));
        Assert.Equal(new[] { "e", "28", "589" }, SiloAnalyzer.Tokens("E 28/589"));
    }

    [Fact]
    public void Accents_fold_and_case_lowers() => Assert.Equal(new[] { "cafe", "nickel" }, SiloAnalyzer.Tokens("Café NICKEL"));
}

public class GeoTests
{
    [Fact]
    public void Mga_to_lat_lon_is_accurate()
    {
        // Perth GPO area, MGA zone 50: about 392 000 E, 6 465 000 N → 31.95 S, 115.86 E.
        var (lat, lon) = GeoText.UtmToLatLon(392_000, 6_465_000, 50, true);
        Assert.InRange(lat, -31.97, -31.93);
        Assert.InRange(lon, 115.84, 115.88);
    }

    [Theory]
    [InlineData('H', 50, 3, 117.0, -29.0, 118.5, -28.0)]   // Kirkalocka
    [InlineData('H', 50, 7, 117.0, -30.0, 118.5, -29.0)]   // Ninghan
    [InlineData('G', 51, 5, 120.0, -26.0, 121.5, -25.0)]   // Nabberu
    public void Map_sheet_boxes(char row, int zone, int n, double w, double s, double e, double north)
        => Assert.Equal(new[] { w, s, e, north }, GeoText.SheetBox(row, zone, n));

    [Fact]
    public void Finds_dms_coordinates()
    {
        var g = GeoText.Find("The prospect is centred at 26°27'34\"S 118°27'00\"E near Garden Gully.");
        Assert.NotNull(g);
        Assert.InRange(g!.Lat!.Value, -26.47, -26.45);
        Assert.InRange(g.Lon!.Value, 118.44, 118.46);
    }

    [Fact]
    public void Finds_map_grid_labels_with_zone()
    {
        var g = GeoText.Find("Coordinate System: GDA 1994 MGA Zone 50  397000 398000 399000 6346000 6347000 6348000");
        Assert.NotNull(g);
        Assert.InRange(g!.Lat!.Value, -33.05, -32.95);
    }

    [Fact]
    public void Falls_back_to_map_sheets()
    {
        var g = GeoText.Find("It straddles the KIRKALOCKA (SH50-03) and NINGHAN (SH50-07) 1:250,000 sheets.");
        Assert.NotNull(g);
        Assert.Equal("map sheet", g!.Basis);
        Assert.Equal(new[] { 117.0, -30.0, 118.5, -28.0 }, g.Bbox);
    }

    [Fact]
    public void Ignores_numbers_without_a_zone() => Assert.Null(GeoText.Find("Sample 6346000 returned 397000 ppb."));
}

public class TabularTests
{
    [Fact]
    public void Recognises_assay_tables()
    {
        var rows = new List<List<string>>
        {
            new() { "HoleID", "From", "To", "Au_ppm", "Cu_ppm" },
            new() { "RC001", "0", "1", "0.12", "45" },
            new() { "RC001", "1", "2", "1.30", "60" },
        };
        var r = new ExtractResult();
        Tabular.Sniff(rows, r);
        Assert.True(r.Fields.TryGetValue("data_type", out var t));
        Assert.Contains("Assays", string.Join(",", t!));
        Assert.Contains(r.Entities, e => e.Value == "RC001");
    }
}

public class DatamineTests
{
    static string Data(string f) => Path.Combine(AppContext.BaseDirectory, "data", f);

    [Theory]
    [InlineData("bm_sp.dm")]
    [InlineData("bm_xp.dm")]
    [InlineData("dh_sp.dm")]
    [InlineData("str_xp.dm")]
    public void Reads_synthetic_files(string file)
    {
        var info = DmProbe.Probe(Data(file), 1 << 20, false);
        Assert.True(info.FieldCount > 0, info.Note);
        Assert.NotNull(info.Fields);
        Assert.True(info.Records > 0);
    }
}
