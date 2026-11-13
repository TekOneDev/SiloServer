using System.Globalization;
using System.Text;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Miscellaneous;
using Lucene.Net.Documents;
using Lucene.Net.Facet;
using Lucene.Net.Facet.SortedSet;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Silo.Plugins;
using Silo.Server.Core;
using LDirectory = Lucene.Net.Store.Directory;

namespace Silo.Server.Index;

/// <summary>Field names of the document index.</summary>
public static class F
{
    public const string Id = "id", IdV = "idv", Coll = "coll", CollV = "collv", CollId = "collid", Dataset = "dataset", Source = "source",
        Name = "name", Alias = "alias", Title = "title", Content = "content", Stem = "stem", Company = "company", CompanyT = "companyt",
        Commodity = "commodity", RType = "rtype", Tenement = "tenement", TenementT = "tenementt", Lith = "lith", Method = "method",
        State = "state", FType = "ftype", Ext = "ext", Year = "year", YearV = "yearv", Size = "size", Mod = "mod", Pages = "pages",
        Ocr = "ocr", HasGeo = "hasgeo", Lat = "lat", Lon = "lon", W = "w", S = "s", E = "e", N = "n", Allow = "allow", Deny = "deny",
        Entity = "entity", HashV = "hashv", Path = "path", Asx = "asx", Owner = "owner", Card = "card";
    // Facet dimensions (kept apart from the fields above).
    public const string DDataset = "f.dataset", DCommodity = "f.commodity", DRType = "f.rtype", DYear = "f.year", DTenement = "f.tenement",
        DCompany = "f.company", DLith = "f.lith", DMethod = "f.method", DState = "f.state", DFType = "f.ftype", DColl = "f.coll",
        DAsx = "f.asx", DOwner = "f.owner", DGeo = "f.geo";
    // Page index.
    public const string PItem = "pitem", PItemV = "pitemv", PNum = "pnum", PText = "ptext", PStem = "pstem";
}

/// <summary>
/// Two Lucene indexes: one document per file (ranking, filters, facets, map) and one per page (which page
/// matched, snippets). Both are rebuilt from the catalog when the schema changes, never from the files.
/// </summary>
public sealed class SearchIndex : IDisposable
{
    public const LuceneVersion V = LuceneVersion.LUCENE_48;
    public const int Schema = 3;
    public static readonly Similarity Sim = new BM25Similarity(1.2f, 0.75f);
    static readonly FieldType StemType = new() { IsIndexed = true, IsTokenized = true, IsStored = false, IndexOptions = IndexOptions.DOCS_AND_FREQS_AND_POSITIONS };
    static readonly FieldType PageTextType = new() { IsIndexed = true, IsTokenized = true, IsStored = true, IndexOptions = IndexOptions.DOCS_AND_FREQS_AND_POSITIONS };

    readonly LDirectory _docDir, _pageDir;
    readonly IndexWriter _docW, _pageW;
    readonly SearcherManager _docs, _pages;
    public FacetsConfig Facets { get; } = new();
    readonly object _facetLock = new();
    (IndexReader? reader, SortedSetDocValuesReaderState? state) _facetState;
    public bool Fresh { get; private set; }

    public static readonly Analyzer Analyzer = new PerFieldAnalyzerWrapper(SiloAnalyzer.Plain, new Dictionary<string, Analyzer> { [F.Stem] = SiloAnalyzer.Stem, [F.PStem] = SiloAnalyzer.Stem });

    /// <summary>Set when the index on disk could not be opened and was started again from empty (it is rebuilt from the catalog).</summary>
    public string? Recovered { get; }

    public SearchIndex(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var vf = System.IO.Path.Combine(dir, "schema.txt");
        Fresh = !File.Exists(vf) || File.ReadAllText(vf).Trim() != Schema.ToString();
        void Wipe()
        {
            foreach (var sub in new[] { "docs", "pages" })
            {
                var p = System.IO.Path.Combine(dir, sub);
                if (System.IO.Directory.Exists(p)) System.IO.Directory.Delete(p, true);
            }
        }
        if (Fresh) Wipe();
        IndexWriterConfig Cfg(double ram) => new(V, Analyzer)
        {
            OpenMode = OpenMode.CREATE_OR_APPEND, Similarity = Sim, RAMBufferSizeMB = ram,
            MergePolicy = new TieredMergePolicy { SegmentsPerTier = 10, MaxMergedSegmentMB = 5 * 1024 },
        };
        (LDirectory, LDirectory, IndexWriter, IndexWriter, SearcherManager, SearcherManager) Open()
        {
            LDirectory dd = FSDirectory.Open(System.IO.Path.Combine(dir, "docs")), pd = FSDirectory.Open(System.IO.Path.Combine(dir, "pages"));
            IndexWriter? dw = null, pw = null;
            try
            {
                dw = new IndexWriter(dd, Cfg(256));
                pw = new IndexWriter(pd, Cfg(512));
                return (dd, pd, dw, pw, new SearcherManager(dw, true, new SimFactory()), new SearcherManager(pw, true, new SimFactory()));
            }
            catch
            {
                try { pw?.Rollback(); } catch { }
                try { dw?.Rollback(); } catch { }
                try { dd.Dispose(); pd.Dispose(); } catch { }
                throw;
            }
        }
        try { (_docDir, _pageDir, _docW, _pageW, _docs, _pages) = Open(); }
        catch (Exception e) when (e is not LockObtainFailedException && !Fresh)
        {
            // A damaged index (a power cut, a full disk) used to stop the service at every start. The catalog holds
            // everything needed to build it again, so start from empty and let the engine reindex (0.1.4).
            Recovered = $"{e.GetType().Name}: {e.Message}";
            Wipe();
            Fresh = true;
            (_docDir, _pageDir, _docW, _pageW, _docs, _pages) = Open();
        }
        foreach (var d in new[] { F.DCommodity, F.DTenement, F.DCompany, F.DLith, F.DMethod, F.DAsx, F.DOwner, F.DFType })
            Facets.SetMultiValued(d, true);
        if (Fresh) { _docW.Commit(); _pageW.Commit(); File.WriteAllText(vf, Schema.ToString()); }
    }

    sealed class SimFactory : SearcherFactory
    {
        public override IndexSearcher NewSearcher(IndexReader r) => new(r) { Similarity = Sim };
    }

    // ---------- writing ----------

    public sealed record Input(ItemRow Item, Derived D, IReadOnlyList<PageText> Pages, Dictionary<string, (FieldSpec spec, string plugin)> Declared);

    public void Delete(long id)
    {
        var t = new Term(F.Id, id.ToString());
        _docW.DeleteDocuments(t);
        _pageW.DeleteDocuments(new Term(F.PItem, id.ToString()));
    }

    public void Put(Input x)
    {
        var it = x.Item; var d = x.D;
        var id = it.Id.ToString();
        var doc = new Document
        {
            new StringField(F.Id, id, Field.Store.YES),
            new NumericDocValuesField(F.IdV, it.Id),
            new StringField(F.Source, it.SourceId, Field.Store.NO),
            new StringField(F.Dataset, it.Dataset.ToLowerInvariant(), Field.Store.NO),
            new TextField(F.Name, it.Name, Field.Store.NO),
            new TextField(F.Alias, Alias(System.IO.Path.GetFileNameWithoutExtension(it.Name)), Field.Store.NO),
            new TextField(F.Path, it.Key, Field.Store.NO),
            new TextField(F.Title, d.DisplayTitle + " \n " + (d.Subtitle ?? "") + " \n " + (it.Title ?? ""), Field.Store.NO),
            new Int64Field(F.Size, it.Size, Field.Store.NO),
            new NumericDocValuesField(F.Size, it.Size),
            new NumericDocValuesField(F.Mod, it.Modified ?? 0),
            new Int32Field(F.Pages, it.Pages, Field.Store.NO),
        };
        // Every facet value is also an exact filter term in a field named after its dimension.
        void Fa(string dim, string v) { doc.Add(new SortedSetDocValuesFacetField(dim, v)); doc.Add(new StringField(dim, v.ToLowerInvariant(), Field.Store.NO)); }
        var collKey = it.Collection != null ? it.Dataset + ":" + it.Collection : "item:" + id;
        doc.Add(new StringField(F.Coll, collKey.ToLowerInvariant(), Field.Store.NO));
        doc.Add(new SortedDocValuesField(F.CollV, new BytesRef(collKey)));
        if (it.Collection != null)
        {
            doc.Add(new StringField(F.CollId, it.Collection.ToLowerInvariant(), Field.Store.NO));
            doc.Add(new TextField(F.Alias, it.Collection, Field.Store.NO));
            Fa(F.DColl, collKey);
        }
        Fa(F.DDataset, d.DatasetLabel);
        if (it.Ext is { Length: > 0 } ext) doc.Add(new StringField(F.Ext, ext.TrimStart('.').ToLowerInvariant(), Field.Store.NO));
        var ftype = FileTypes.Group(it.Ext, it.Name);
        doc.Add(new StringField(F.FType, ftype.ToLowerInvariant(), Field.Store.NO));
        Fa(F.DFType, ftype);
        if (it.TextHash != null) doc.Add(new SortedDocValuesField(F.HashV, new BytesRef(it.TextHash)));
        else doc.Add(new SortedDocValuesField(F.HashV, new BytesRef("~" + id)));
        if (it.OcrPages > 0) doc.Add(new StringField(F.Ocr, "1", Field.Store.NO));

        foreach (var a in it.Allow) doc.Add(new StringField(F.Allow, a.ToLowerInvariant(), Field.Store.NO));
        foreach (var a in it.Deny) doc.Add(new StringField(F.Deny, a.ToLowerInvariant(), Field.Store.NO));

        void Kw(string field, string dim, IEnumerable<string> values, bool text = false)
        {
            foreach (var v in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Fa(dim, v.Length > 120 ? v[..120] : v);
            }
        }
        Kw(F.Commodity, F.DCommodity, d.Commodities);
        if (d.ReportType != null) Kw(F.RType, F.DRType, [d.ReportType]);
        Kw(F.Tenement, F.DTenement, d.Tenements);
        foreach (var t in d.Tenements) doc.Add(new TextField(F.TenementT, t, Field.Store.NO));
        Kw(F.Company, F.DCompany, d.Companies);
        foreach (var c in d.Companies) doc.Add(new TextField(F.CompanyT, c, Field.Store.NO));
        Kw(F.Lith, F.DLith, d.Lithology);
        Kw(F.Method, F.DMethod, d.Methods);
        if (d.State != null) Kw(F.State, F.DState, [d.State]);
        if (d.Year is { } y)
        {
            doc.Add(new Int32Field(F.Year, y, Field.Store.NO));
            doc.Add(new NumericDocValuesField(F.YearV, y));
            Fa(F.DYear, y.ToString());
        }
        // CSV lookups (ASX companies).
        if (d.Lookup.TryGetValue("asx_code", out var asx)) Kw(F.Asx, F.DAsx, asx.Where(a => a.Length > 0).Select(a => a.ToUpperInvariant()));
        var owner = d.Lookup.GetValueOrDefault("company_now") ?? d.Lookup.GetValueOrDefault("company_listed");
        if (owner != null) { Kw(F.Owner, F.DOwner, owner); foreach (var o in owner) doc.Add(new TextField(F.CompanyT, o, Field.Store.NO)); }

        // Plugin fields, as declared in their manifests.
        foreach (var (name, values) in it.Fields)
        {
            var key = "x." + name;
            if (!x.Declared.TryGetValue(key, out var decl)) continue;
            foreach (var v in values.Where(v => !string.IsNullOrWhiteSpace(v)))
            {
                if (decl.spec.Type is "keyword" or "number") doc.Add(new StringField(key, v.ToLowerInvariant(), Field.Store.NO));
                if (decl.spec.Searchable) doc.Add(new TextField(F.Entity, v, Field.Store.NO));
                if (decl.spec.Facet) { Facets.SetMultiValued(key, true); doc.Add(new SortedSetDocValuesFacetField(key, v.Length > 120 ? v[..120] : v)); }
            }
        }
        foreach (var en in it.Entities.Take(2000)) doc.Add(new TextField(F.Entity, en.Value, Field.Store.NO));

        // Location.
        if (d.Geo is { Lat: { } lat, Lon: { } lon } g)
        {
            var bb = g.Bbox is { Length: 4 } b ? b : [lon, lat, lon, lat];
            doc.Add(new StringField(F.HasGeo, "1", Field.Store.NO));
            doc.Add(new DoubleField(F.W, bb[0], Field.Store.NO)); doc.Add(new DoubleField(F.S, bb[1], Field.Store.NO));
            doc.Add(new DoubleField(F.E, bb[2], Field.Store.NO)); doc.Add(new DoubleField(F.N, bb[3], Field.Store.NO));
            doc.Add(new DoubleDocValuesField(F.Lat, lat)); doc.Add(new DoubleDocValuesField(F.Lon, lon));
            Fa(F.DGeo, g.Basis switch { "text" => "From report text", "map sheet" => "From map sheet (approx.)", "table" or "exif" => "From file data", _ => "Report footprint" });
        }
        else
        {
            doc.Add(new DoubleDocValuesField(F.Lat, double.NaN)); doc.Add(new DoubleDocValuesField(F.Lon, double.NaN));
            Fa(F.DGeo, "No location");
        }

        // Text: all pages for ranking. The stemmed field also holds the titles so "reports" finds "Report".
        var all = new StringBuilder();
        foreach (var p in x.Pages) { all.Append(p.Text).Append('\n'); if (all.Length > 20_000_000) break; }
        if (it.Text != null) all.Append(it.Text);
        if (it.Summary != null) all.Append('\n').Append(it.Summary);
        var text = all.ToString();
        if (text.Length > 0) doc.Add(new TextField(F.Content, text, Field.Store.NO));
        doc.Add(new Field(F.Stem, d.DisplayTitle + "\n" + (d.Subtitle ?? "") + "\n" + text, StemType));

        doc.Add(new StoredField(F.Card, Card(x, ftype)));
        _docW.UpdateDocument(new Term(F.Id, id), Facets.Build(doc));

        // Pages.
        _pageW.DeleteDocuments(new Term(F.PItem, id));
        var pageDocs = new List<Document>();
        foreach (var p in x.Pages)
        {
            if (string.IsNullOrWhiteSpace(p.Text)) continue;
            pageDocs.Add(new Document
            {
                new StringField(F.PItem, id, Field.Store.NO),
                new NumericDocValuesField(F.PItemV, it.Id),
                new StoredField(F.PNum, p.N),
                new NumericDocValuesField(F.PNum, p.N),
                new Field(F.PText, p.Text, PageTextType),
                new Field(F.PStem, p.Text, StemType),
            });
        }
        if (it.Text != null) pageDocs.Add(new Document { new StringField(F.PItem, id, Field.Store.NO), new NumericDocValuesField(F.PItemV, it.Id), new StoredField(F.PNum, 1), new NumericDocValuesField(F.PNum, 1), new Field(F.PText, it.Text, PageTextType), new Field(F.PStem, it.Text, StemType) });
        if (pageDocs.Count > 0) _pageW.AddDocuments(pageDocs);
    }

    /// <summary>What a result card shows, stored with the document so search never goes back to the catalog.</summary>
    static string Card(Input x, string ftype)
    {
        var it = x.Item; var d = x.D;
        var shown = new List<object>();
        foreach (var (name, values) in it.Fields)
            if (x.Declared.TryGetValue("x." + name, out var decl) && decl.spec.Show && values.Count > 0)
                shown.Add(new { l = decl.spec.Label, v = string.Join(", ", values.Take(4)) });
        var card = new Dictionary<string, object?>
        {
            ["title"] = d.DisplayTitle, ["sub"] = d.Subtitle, ["name"] = it.Name, ["ext"] = it.Ext, ["ftype"] = ftype, ["ds"] = d.DatasetLabel,
            ["dsid"] = it.Dataset, ["coll"] = it.Collection, ["year"] = d.Year, ["company"] = d.Companies, ["commodity"] = d.Commodities, ["rtype"] = d.ReportType,
            ["ten"] = d.Tenements.Take(12).ToList(), ["lith"] = d.Lithology.Take(6).ToList(), ["method"] = d.Methods.Take(6).ToList(), ["state"] = d.State,
            ["size"] = it.Size, ["pages"] = it.Pages, ["ocr"] = it.OcrPages, ["mod"] = it.Modified,
            ["lat"] = d.Geo?.Lat, ["lon"] = d.Geo?.Lon, ["bbox"] = d.Geo?.Bbox, ["geo"] = d.Geo?.Basis, ["raw"] = d.Geo?.Raw,
            ["licence"] = it.M("licence"), ["attr"] = it.M("attribution"), ["landing"] = it.M("landing_page") ?? it.Url, ["summary"] = it.Summary,
            ["fields"] = shown, ["path"] = it.Path, ["loc"] = it.Location, ["src"] = it.SourceId, ["state_"] = it.State, ["err"] = it.Error,
            ["asx"] = d.Lookup.GetValueOrDefault("asx_code")?.Where(a => a.Length > 0).Distinct().ToList(),
            ["owner"] = (d.Lookup.GetValueOrDefault("company_now") ?? d.Lookup.GetValueOrDefault("company_listed"))?.Distinct().ToList(),
            ["cstatus"] = d.Lookup.GetValueOrDefault("company_status")?.Distinct().ToList(),
        };
        return System.Text.Json.JsonSerializer.Serialize(card, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    /// <summary>File-name words: camelCase and letter/number runs split ("DrillPlan2" → "Drill Plan 2").</summary>
    public static string Alias(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0)
            {
                var p = name[i - 1];
                if ((char.IsUpper(c) && char.IsLower(p)) || (char.IsDigit(c) != char.IsDigit(p) && char.IsLetterOrDigit(c) && char.IsLetterOrDigit(p))) sb.Append(' ');
            }
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return sb.ToString();
    }

    public void Refresh() { _docs.MaybeRefresh(); _pages.MaybeRefresh(); }
    public void RefreshBlocking() { _docs.MaybeRefreshBlocking(); _pages.MaybeRefreshBlocking(); }
    public void Commit() { _docW.Commit(); _pageW.Commit(); }

    public void DeleteAll() { _docW.DeleteAll(); _pageW.DeleteAll(); Commit(); RefreshBlocking(); }

    // ---------- reading ----------

    public T WithDocs<T>(Func<IndexSearcher, T> f)
    {
        var s = _docs.Acquire();
        try { return f(s); } finally { _docs.Release(s); }
    }

    public T WithPages<T>(Func<IndexSearcher, T> f)
    {
        var s = _pages.Acquire();
        try { return f(s); } finally { _pages.Release(s); }
    }

    public int DocCount => WithDocs(s => s.IndexReader.NumDocs);
    public int PageCount => WithPages(s => s.IndexReader.NumDocs);

    /// <summary>Build the facet tables for the current reader now, so the next search does not pay for it.</summary>
    public void Warm() { try { WithDocs(s => FacetState(s.IndexReader)); } catch { } }

    public SortedSetDocValuesReaderState? FacetState(IndexReader reader)
    {
        lock (_facetLock)
        {
            if (ReferenceEquals(_facetState.reader, reader)) return _facetState.state;
            SortedSetDocValuesReaderState? st = null;
            try { st = new DefaultSortedSetDocValuesReaderState(reader); } catch (Exception) { }
            _facetState = (reader, st);
            return st;
        }
    }

    public long SizeOnDisk()
    {
        long n = 0;
        foreach (var d in new[] { _docDir, _pageDir })
            if (d is FSDirectory fs) try { n += new DirectoryInfo(fs.Directory.FullName).EnumerateFiles().Sum(f => f.Length); } catch { }
        return n;
    }

    public void Dispose()
    {
        try { Commit(); } catch { }
        _docs.Dispose(); _pages.Dispose();
        _docW.Dispose(); _pageW.Dispose();
        _docDir.Dispose(); _pageDir.Dispose();
    }
}

public static class FileTypes
{
    public static string Group(string? ext, string name)
    {
        var e = (ext ?? "").TrimStart('.').ToLowerInvariant();
        return e switch
        {
            "pdf" => "PDF",
            "doc" or "docx" or "docm" or "dotx" or "rtf" or "odt" => "Word document",
            "xls" or "xlsx" or "xlsm" or "ods" => "Spreadsheet",
            "csv" or "tsv" or "dat" or "asc" or "prn" => "Data table",
            "ppt" or "pptx" or "pptm" or "odp" => "Presentation",
            "jpg" or "jpeg" or "png" or "tif" or "tiff" or "bmp" or "gif" or "webp" or "heic" or "ecw" => "Image or scan",
            "txt" or "md" or "log" => "Text",
            "las" => "Well log (LAS)",
            "htm" or "html" or "xml" or "json" or "kml" => "Web / data file",
            "dm" or "dmx" => "Datamine",
            "rs3compute" or "rs3v3" or "rs3results" or "rs3log" => "Rocscience RS3",
            "dxf" or "dwg" => "CAD drawing",
            "shp" or "tab" or "mif" or "gpkg" or "geojson" => "GIS data",
            "zip" or "7z" or "rar" => "Archive",
            "" when name.Equals("engine", StringComparison.OrdinalIgnoreCase) => "Rocscience RS3",
            _ => "Other",
        };
    }
}
