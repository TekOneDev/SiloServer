using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lucene.Net.Facet;
using Lucene.Net.Facet.SortedSet;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Spell;
using Lucene.Net.Util;
using Silo.Server.Core;

namespace Silo.Server.Index;

/// <summary>Who is searching: the permission tokens their results are trimmed to.</summary>
public sealed class Principal
{
    public string Name { get; init; } = "anonymous";
    public List<string> Tokens { get; init; } = new();
    public bool IsAdmin { get; init; }
    public string Key => string.Join("|", Tokens.OrderBy(t => t));
}

public sealed class SearchRequest
{
    public string Q { get; set; } = "";
    public Dictionary<string, List<string>>? Filters { get; set; }
    public Dictionary<string, List<string>>? Not { get; set; }
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    public double[]? Bbox { get; set; }
    public string Sort { get; set; } = "relevance";   // relevance | newest | oldest
    public int Offset { get; set; }
    public int Limit { get; set; } = 10;
    public bool Facets { get; set; } = true;
    public bool Group { get; set; } = true;
    /// <summary>One facet dimension to return in full (the "more" link).</summary>
    public string? FacetAll { get; set; }
}

public sealed class Hit
{
    public long Id { get; set; }
    public JsonNode? Card { get; set; }
    public int? Page { get; set; }
    public string? Snippet { get; set; }
    public float Score { get; set; }
    public int Copies { get; set; }
}

public sealed class Group
{
    public string Key { get; set; } = "";
    public Hit Top { get; set; } = null!;
    public List<Hit> More { get; set; } = new();
    public int Files { get; set; }
}

public sealed record FacetCount(string Value, long Count);

public sealed class SearchResponse
{
    public string Q { get; set; } = "";
    public long Total { get; set; }
    public long Reports { get; set; }
    public double TookMs { get; set; }
    public List<Group> Groups { get; set; } = new();
    public Dictionary<string, List<FacetCount>> Facets { get; set; } = new();
    public List<Chip> Chips { get; set; } = new();
    public string? DidYouMean { get; set; }
    public bool Relaxed { get; set; }
    public bool Building { get; set; }
    public bool More { get; set; }
}

/// <summary>
/// Search over the document index: BM25 with field boosts (report id and tenement first, then titles, names,
/// companies, entities, text, stemmed text), mining synonyms, all-words-first, permission trimming,
/// faceting with drill-sideways counts, report grouping, duplicate folding and page-level snippets.
/// </summary>
public sealed class Searcher
{
    readonly SearchIndex _ix;
    readonly Rules _rules;
    readonly Func<bool> _building;
    readonly Dictionary<string, Filter> _aclCache = new();

    public Searcher(SearchIndex ix, Rules rules, Func<bool> building) { _ix = ix; _rules = rules; _building = building; }

    public static readonly string[] FacetDims = [F.DDataset, F.DCommodity, F.DRType, F.DYear, F.DState, F.DTenement, F.DCompany, F.DOwner, F.DAsx, F.DLith, F.DMethod, F.DFType, F.DGeo];

    public List<string> ExtraDims { get; set; } = new();

    // ---------- query building ----------

    const float WCollId = 40f, WTenement = 25f, WTitle = 5f, WName = 3f, WAlias = 2.5f, WCompany = 3f, WEntity = 2f, WContent = 1f, WStem = 0.6f, WSyn = 0.5f, WPath = 0.5f;

    Query? TextQuery(ParsedQuery q, bool strict)
    {
        if (!q.HasText && q.Tenements.Count == 0 && q.ReportIds.Count == 0) return null;
        var main = new BooleanQuery(true);
        var words = new List<Query>();
        string? lastPlain = q.LastIsPrefix ? q.Clauses.SelectMany(c => c).LastOrDefault(t => !t.StartsWith('"')) : null;
        // A typed tenement ("E 8/1622", "E08/1622") becomes one exact clause instead of loose words.
        static string Nz(string x) => x.Length > 1 && x.All(char.IsDigit) ? x.TrimStart('0').PadLeft(1, '0') : x;
        var tenToks = q.Tenements.Select(t => SiloAnalyzer.Tokens(t)).ToList();
        var tenSet = tenToks.SelectMany(x => x).Select(Nz).ToHashSet();
        foreach (var clause in q.Clauses)
        {
            if (tenSet.Count > 0 && clause.Count == 1 && !clause[0].StartsWith('"') && SiloAnalyzer.Tokens(clause[0]) is { Count: > 0 } ct && ct.All(x => tenSet.Contains(Nz(x)))) continue;
            var alts = clause.Select(t => Word(t, ReferenceEquals(t, lastPlain))).Where(x => x != null).ToList();
            if (alts.Count == 0) continue;
            if (alts.Count == 1) words.Add(alts[0]!);
            else { var or = new BooleanQuery(true); foreach (var a in alts) or.Add(a, Occur.SHOULD); words.Add(or); }
        }
        if (tenToks.Count > 0)
        {
            var anyTen = new BooleanQuery(true);
            for (int i = 0; i < tenToks.Count; i++)
            {
                anyTen.Add(new TermQuery(new Term(F.DTenement, q.Tenements[i].ToLowerInvariant())) { Boost = WTenement }, Occur.SHOULD);
                foreach (var (f, b) in new[] { (F.Content, WContent), (F.Title, WTitle), (F.TenementT, WTitle) })
                {
                    var mpq = new MultiPhraseQuery { Boost = b };
                    foreach (var tok in tenToks[i])
                    {
                        // District numbers are written with and without the leading zero (E8/1622, E08/1622).
                        var nz = Nz(tok);
                        mpq.Add(nz.All(char.IsDigit) && nz.Length == 1 ? new[] { new Term(f, nz), new Term(f, "0" + nz) } : new[] { new Term(f, tok) });
                    }
                    anyTen.Add(mpq, Occur.SHOULD);
                }
            }
            words.Add(anyTen);
        }
        if (words.Count == 0 && q.Tenements.Count == 0 && q.ReportIds.Count == 0) return null;
        if (!strict && words.Count >= 2)
        {
            var some = new BooleanQuery(true) { MinimumNumberShouldMatch = Math.Max(1, words.Count - (words.Count >= 4 ? 2 : 1)) };
            foreach (var w in words) some.Add(w, Occur.SHOULD);
            main.Add(some, Occur.MUST);
        }
        else foreach (var w in words) main.Add(w, Occur.MUST);
        // A typed tenement or report id: those documents first (they match the words anyway).
        foreach (var r in q.ReportIds) main.Add(new TermQuery(new Term(F.CollId, r.ToLowerInvariant())) { Boost = WCollId }, Occur.SHOULD);
        foreach (var e in q.Excluded) if (Word(e, false) is { } x) main.Add(x, Occur.MUST_NOT);
        if (words.Count == 0) { var any = new BooleanQuery(true); foreach (var c in main.Clauses.Where(c => c.Occur == Occur.SHOULD)) any.Add(c.Query, Occur.SHOULD); return any; }
        return main;
    }

    static readonly (string f, float b)[] ExactFields = [(F.Title, WTitle), (F.Name, WName), (F.Alias, WAlias), (F.CompanyT, WCompany), (F.TenementT, WTitle), (F.Entity, WEntity), (F.Content, WContent), (F.Path, WPath)];
    static readonly (string f, float b)[] PhraseFields = [(F.Title, WTitle), (F.Name, WName), (F.Alias, WAlias), (F.CompanyT, WCompany), (F.TenementT, WTitle), (F.Entity, WEntity), (F.Content, WContent * 1.3f)];

    Query? Word(string term, bool prefix)
    {
        bool quoted = term.StartsWith('"');
        var raw = term.Trim('"');
        var toks = SiloAnalyzer.Tokens(raw);
        if (toks.Count == 0) return null;
        var dm = new DisjunctionMaxQuery(0.15f);
        if (toks.Count == 1 && !quoted)
        {
            var t = toks[0];
            foreach (var (f, b) in ExactFields) dm.Add(new TermQuery(new Term(f, t)) { Boost = b });
            foreach (var st in SiloAnalyzer.Tokens(t, true)) dm.Add(new TermQuery(new Term(F.Stem, st)) { Boost = WStem });
            if (prefix && t.Length >= 3)
            {
                dm.Add(new ConstantScoreQuery(new PrefixQuery(new Term(F.Title, t))) { Boost = 1.5f });
                dm.Add(new ConstantScoreQuery(new PrefixQuery(new Term(F.Content, t))) { Boost = 0.4f });
            }
        }
        else
        {
            foreach (var (f, b) in PhraseFields)
            {
                var pq = new PhraseQuery { Boost = b };
                foreach (var t in toks) pq.Add(new Term(f, t));
                dm.Add(pq);
            }
            if (!quoted)
            {
                var spq = new PhraseQuery { Boost = WStem };
                foreach (var t in toks) foreach (var st in SiloAnalyzer.Tokens(t, true)) spq.Add(new Term(F.Stem, st));
                dm.Add(spq);
            }
        }
        // Mining synonyms (RC = reverse circulation, BIF = banded iron formation, Au = gold, sulphide = sulfide…).
        if (!quoted && _rules.SynonymMap.TryGetValue(raw, out var syns))
            foreach (var s in syns)
            {
                var st = SiloAnalyzer.Tokens(s);
                if (st.Count == 1) { dm.Add(new TermQuery(new Term(F.Content, st[0])) { Boost = WSyn }); dm.Add(new TermQuery(new Term(F.Title, st[0])) { Boost = WSyn * WTitle }); }
                else if (st.Count > 1)
                {
                    var pq = new PhraseQuery { Boost = WSyn }; foreach (var t in st) pq.Add(new Term(F.Content, t)); dm.Add(pq);
                    var tq = new PhraseQuery { Boost = WSyn * WTitle }; foreach (var t in st) tq.Add(new Term(F.Title, t)); dm.Add(tq);
                }
            }
        return dm;
    }

    Query FilterQuery(ParsedQuery q, Principal who, string? skipDim = null)
    {
        var f = new BooleanQuery(true);
        // Permissions: an allow token must match and no deny token may. Items without allows match nothing.
        var allow = new BooleanQuery(true);
        foreach (var t in who.Tokens) allow.Add(new TermQuery(new Term(F.Allow, t.ToLowerInvariant())), Occur.SHOULD);
        f.Add(allow, Occur.MUST);
        foreach (var t in who.Tokens) f.Add(new TermQuery(new Term(F.Deny, t.ToLowerInvariant())), Occur.MUST_NOT);

        foreach (var (dim, values) in q.Filters)
        {
            if (dim == skipDim || values.Count == 0) continue;
            var field = dim switch { "ext" => F.Ext, "collid" => F.CollId, _ => dim };
            var any = new BooleanQuery(true);
            foreach (var v in values) any.Add(new TermQuery(new Term(field, v.ToLowerInvariant())), Occur.SHOULD);
            f.Add(any, Occur.MUST);
        }
        foreach (var (dim, values) in q.NotFilters)
            foreach (var v in values) f.Add(new TermQuery(new Term(dim switch { "ext" => F.Ext, "collid" => F.CollId, _ => dim }, v.ToLowerInvariant())), Occur.MUST_NOT);
        if ((q.YearFrom != null || q.YearTo != null) && skipDim != F.DYear)
            f.Add(NumericRangeQuery.NewInt32Range(F.Year, q.YearFrom ?? 0, q.YearTo ?? 9999, true, true), Occur.MUST);
        if (q.HasGeo is { } hg) f.Add(new TermQuery(new Term(F.HasGeo, "1")), hg ? Occur.MUST : Occur.MUST_NOT);
        if (q.Ocr is { } oc) f.Add(new TermQuery(new Term(F.Ocr, "1")), oc ? Occur.MUST : Occur.MUST_NOT);
        if (q.Bbox is { Length: 4 } b)
        {
            // The item's extent intersects the area.
            f.Add(NumericRangeQuery.NewDoubleRange(F.W, null, b[2], true, true), Occur.MUST);
            f.Add(NumericRangeQuery.NewDoubleRange(F.E, b[0], null, true, true), Occur.MUST);
            f.Add(NumericRangeQuery.NewDoubleRange(F.S, null, b[3], true, true), Occur.MUST);
            f.Add(NumericRangeQuery.NewDoubleRange(F.N, b[1], null, true, true), Occur.MUST);
        }
        return f;
    }

    public ParsedQuery Prepare(SearchRequest req)
    {
        var q = QueryParser.Parse(req.Q, _rules);
        if (req.Filters != null) foreach (var (dim, vs) in req.Filters) foreach (var v in vs) q.AddFilter(dim, v);
        if (req.Not != null) foreach (var (dim, vs) in req.Not) foreach (var v in vs) q.AddFilter(dim, v, true);
        if (req.YearFrom != null) q.YearFrom = req.YearFrom;
        if (req.YearTo != null) q.YearTo = req.YearTo;
        if (req.Bbox is { Length: 4 }) q.Bbox = req.Bbox;
        return q;
    }

    // ---------- search ----------

    public SearchResponse Search(SearchRequest req, Principal who, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var q = Prepare(req);
        var res = new SearchResponse { Q = req.Q, Chips = q.Chips, Building = _building() };
        var text = TextQuery(q, strict: true);
        var filter = FilterQuery(q, who);
        return _ix.WithDocs(s =>
        {
            Query main = text ?? new MatchAllDocsQuery();
            Sort? sort = req.Sort switch
            {
                "newest" => new Sort(new SortField(F.YearV, SortFieldType.INT32, true), SortField.FIELD_SCORE),
                "oldest" => new Sort(new SortField(F.YearV, SortFieldType.INT32, false), SortField.FIELD_SCORE),
                _ when text == null => new Sort(new SortField(F.YearV, SortFieldType.INT32, true)),
                _ => null,
            };
            int pool = Math.Min(5000, Math.Max(400, (req.Offset + req.Limit) * 8));
            var qf = new QueryWrapperFilter(filter);
            TopDocs top = sort == null ? s.Search(main, qf, pool) : s.Search(main, qf, pool, sort, true, false);
            // All words first; if almost nothing has them all, let a word or two be missing.
            // Not for a tenement or report id that was found: other permits would only dilute an exact lookup.
            bool exactId = top.TotalHits > 0 && (q.Tenements.Count > 0 || q.ReportIds.Count > 0);
            if (top.TotalHits < 5 && !exactId && q.Clauses.Count >= 2 && TextQuery(q, strict: false) is { } loose)
            {
                var top2 = sort == null ? s.Search(loose, qf, pool) : s.Search(loose, qf, pool, sort, true, false);
                if (top2.TotalHits > top.TotalHits) { top = top2; main = loose; res.Relaxed = true; }
            }
            res.Total = top.TotalHits;

            // Group by report, fold identical copies, page through groups.
            var reader = s.IndexReader;
            var coll = MultiDocValues.GetSortedValues(reader, F.CollV);
            var hash = MultiDocValues.GetSortedValues(reader, F.HashV);
            var groups = new List<(string key, List<(int doc, float score)> docs)>();
            var byKey = new Dictionary<string, int>();
            var seenHash = new Dictionary<string, Hit>();
            var copies = new Dictionary<int, int>();
            var br = new BytesRef();
            var primary = new Dictionary<string, int>();
            foreach (var sd in top.ScoreDocs)
            {
                string hk = "";
                if (hash != null) { hash.Get(sd.Doc, br); hk = br.Utf8ToString(); }
                if (hk.Length > 0 && !hk.StartsWith('~') && primary.TryGetValue(hk, out var first)) { copies[first] = copies.GetValueOrDefault(first) + 1; continue; }
                if (hk.Length > 0) primary[hk] = sd.Doc;
                string key = sd.Doc.ToString();
                if (req.Group && coll != null) { coll.Get(sd.Doc, br); key = br.Utf8ToString(); }
                if (!byKey.TryGetValue(key, out var gi)) { byKey[key] = gi = groups.Count; groups.Add((key, new())); }
                groups[gi].docs.Add((sd.Doc, float.IsNaN(sd.Score) ? 0 : sd.Score));
            }
            res.More = groups.Count > req.Offset + req.Limit || (top.TotalHits > top.ScoreDocs.Length && groups.Count >= req.Offset + req.Limit);
            var page = groups.Skip(req.Offset).Take(req.Limit).ToList();
            var keep = new HashSet<string> { F.Id, F.Card };
            foreach (var (key, docs) in page)
            {
                Hit Make((int doc, float score) d)
                {
                    var stored = s.Doc(d.doc, keep);
                    return new Hit { Id = long.Parse(stored.Get(F.Id)), Card = JsonNode.Parse(stored.Get(F.Card) ?? "{}"), Score = d.score, Copies = copies.GetValueOrDefault(d.doc) };
                }
                var g = new Group { Key = key, Top = Make(docs[0]), Files = docs.Count };
                foreach (var d in docs.Skip(1).Take(4)) g.More.Add(Make(d));
                res.Groups.Add(g);
            }
            Snippets(q, res.Groups.SelectMany(g => g.More.Prepend(g.Top)).ToList());

            if (req.Facets)
            {
                res.Facets = Facets(s, main, q, who, req.FacetAll);
                if (res.Facets.TryGetValue("reports", out var rc) && rc.Count > 0) res.Reports = rc[0].Count;
                res.Facets.Remove("reports");
            }
            if (res.Total < 3 && q.HasText) res.DidYouMean = Suggest(reader, q);
            res.TookMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
            return res;
        });
    }

    Dictionary<string, List<FacetCount>> Facets(IndexSearcher s, Query main, ParsedQuery q, Principal who, string? all)
    {
        var res = new Dictionary<string, List<FacetCount>>();
        var state = _ix.FacetState(s.IndexReader);
        if (state == null) return res;
        var dims = FacetDims.Concat(ExtraDims).Distinct().ToList();
        var known = new HashSet<string>(state.PrefixToOrdRange.Keys);
        dims = dims.Where(known.Contains).ToList();

        FacetsCollector Collect(string? skip)
        {
            var fc = new FacetsCollector();
            FacetsCollector.Search(s, main, new QueryWrapperFilter(FilterQuery(q, who, skip)), 1, fc);
            return fc;
        }
        var baseCounts = new SortedSetDocValuesFacetCounts(state, Collect(null));
        // A dimension that is filtered counts as if its own filter were off, so its other values stay visible.
        var active = q.Filters.Keys.Where(dims.Contains).ToList();
        if ((q.YearFrom != null || q.YearTo != null) && !active.Contains(F.DYear)) active.Add(F.DYear);
        var sideways = active.ToDictionary(d => d, d => new SortedSetDocValuesFacetCounts(state, Collect(d)));
        foreach (var dim in dims)
        {
            var counts = sideways.GetValueOrDefault(dim) ?? baseCounts;
            int n = dim == all ? 500 : dim == F.DYear ? 300 : dim == F.DTenement || dim == F.DCompany ? 15 : 12;
            FacetResult? fr = null;
            try { fr = counts.GetTopChildren(n, dim); } catch (ArgumentException) { }
            if (fr == null) continue;
            res[dim] = fr.LabelValues.Select(lv => new FacetCount(lv.Label, (long)lv.Value)).ToList();
            res[dim + ":n"] = [new FacetCount("distinct", fr.ChildCount)];
        }
        if (known.Contains(F.DColl))
        {
            try { var fr = baseCounts.GetTopChildren(1, F.DColl); if (fr != null) res["reports"] = [new FacetCount("reports", fr.ChildCount)]; } catch (ArgumentException) { }
        }
        return res;
    }

    string? Suggest(IndexReader reader, ParsedQuery q)
    {
        var spell = new DirectSpellChecker { MinQueryLength = 4, MaxEdits = 2, Accuracy = 0.7f };
        bool changed = false;
        var parts = new List<string>();
        foreach (var clause in q.Clauses)
        {
            var t = clause[0];
            if (t.StartsWith('"') || t.Any(char.IsDigit)) { parts.Add(t); continue; }
            var tok = SiloAnalyzer.Tokens(t);
            if (tok.Count != 1) { parts.Add(t); continue; }
            var sug = spell.SuggestSimilar(new Term(F.Content, tok[0]), 1, reader, SuggestMode.SUGGEST_WHEN_NOT_IN_INDEX);
            if (sug.Length > 0 && sug[0].String != tok[0]) { parts.Add(sug[0].String); changed = true; }
            else
            {
                var more = spell.SuggestSimilar(new Term(F.Content, tok[0]), 1, reader, SuggestMode.SUGGEST_MORE_POPULAR);
                if (more.Length > 0 && more[0].Freq > 20 * Math.Max(1, reader.DocFreq(new Term(F.Content, tok[0])))) { parts.Add(more[0].String); changed = true; }
                else parts.Add(t);
            }
        }
        return changed ? string.Join(' ', parts) : null;
    }

    // ---------- snippets ----------

    Query? PageQuery(ParsedQuery q)
    {
        var bq = new BooleanQuery(true);
        foreach (var t in q.Clauses.SelectMany(c => c))
        {
            var toks = SiloAnalyzer.Tokens(t.Trim('"'));
            if (toks.Count == 0) continue;
            if (toks.Count == 1)
            {
                var dm = new DisjunctionMaxQuery(0.1f) { new TermQuery(new Term(F.PText, toks[0])) };
                foreach (var st in SiloAnalyzer.Tokens(toks[0], true)) dm.Add(new TermQuery(new Term(F.PStem, st)) { Boost = 0.7f });
                if (!t.StartsWith('"') && _rules.SynonymMap.TryGetValue(t, out var syn))
                    foreach (var sy in syn) { var sts = SiloAnalyzer.Tokens(sy); if (sts.Count == 1) dm.Add(new TermQuery(new Term(F.PText, sts[0])) { Boost = 0.5f }); else { var p2 = new PhraseQuery { Boost = 0.5f }; foreach (var x in sts) p2.Add(new Term(F.PText, x)); dm.Add(p2); } }
                bq.Add(dm, Occur.SHOULD);
            }
            else
            {
                var pq = new PhraseQuery { Boost = 2f };
                foreach (var x in toks) pq.Add(new Term(F.PText, x));
                bq.Add(pq, Occur.SHOULD);
            }
        }
        foreach (var ten in q.Tenements)
        {
            var toks = SiloAnalyzer.Tokens(ten);
            var pq = new PhraseQuery { Boost = 3f };
            foreach (var x in toks) pq.Add(new Term(F.PText, x));
            bq.Add(pq, Occur.SHOULD);
        }
        return bq.Clauses.Count > 0 ? bq : null;
    }

    void Snippets(ParsedQuery q, List<Hit> hits)
    {
        if (hits.Count == 0) return;
        var terms = Highlight.Terms(q, _rules);
        var pq = PageQuery(q);
        _ix.WithPages(ps =>
        {
            var ids = new BooleanQuery(true);
            foreach (var h in hits) ids.Add(new TermQuery(new Term(F.PItem, h.Id.ToString())), Occur.SHOULD);
            var best = new Dictionary<long, (int page, string text)>();
            var itemv = MultiDocValues.GetNumericValues(ps.IndexReader, F.PItemV);
            if (pq != null)
            {
                var top = ps.Search(pq, new QueryWrapperFilter(ids), hits.Count * 30);
                foreach (var sd in top.ScoreDocs)
                {
                    long item = itemv?.Get(sd.Doc) ?? 0;
                    if (best.ContainsKey(item)) continue;
                    var d = ps.Doc(sd.Doc);
                    best[item] = (d.GetField(F.PNum)?.GetInt32Value() ?? 1, d.Get(F.PText) ?? "");
                }
            }
            foreach (var h in hits)
            {
                if (best.TryGetValue(h.Id, out var b)) { h.Page = b.page; h.Snippet = Highlight.Snippet(b.text, terms); continue; }
                // Matched on metadata only: lead with the file's own summary or first page.
                var summary = h.Card?["summary"]?.ToString();
                if (!string.IsNullOrEmpty(summary)) { h.Snippet = Highlight.Snippet(summary, terms); continue; }
                var first = ps.Search(new TermQuery(new Term(F.PItem, h.Id.ToString())), null, 1, new Sort(new SortField(F.PNum, SortFieldType.INT64)));
                if (first.ScoreDocs.Length > 0) { var d = ps.Doc(first.ScoreDocs[0].Doc); h.Snippet = Highlight.Snippet(d.Get(F.PText) ?? "", terms); }
            }
            return 0;
        });
    }

    // ---------- other reads ----------

    /// <summary>One item's card, if the principal may see it.</summary>
    public Hit? Get(long id, Principal who) => _ix.WithDocs(s =>
    {
        var bq = new BooleanQuery(true) { { new TermQuery(new Term(F.Id, id.ToString())), Occur.MUST } };
        var top = s.Search(bq, new QueryWrapperFilter(FilterQuery(new ParsedQuery(), who)), 1);
        if (top.ScoreDocs.Length == 0) return null;
        var d = s.Doc(top.ScoreDocs[0].Doc);
        return new Hit { Id = id, Card = JsonNode.Parse(d.Get(F.Card) ?? "{}") };
    });

    /// <summary>The files of one report that the principal may see.</summary>
    public List<Hit> Collection(string key, Principal who) => _ix.WithDocs(s =>
    {
        var top = s.Search(new TermQuery(new Term(F.Coll, key.ToLowerInvariant())), new QueryWrapperFilter(FilterQuery(new ParsedQuery(), who)), 500);
        return top.ScoreDocs.Select(sd => { var d = s.Doc(sd.Doc); return new Hit { Id = long.Parse(d.Get(F.Id)), Card = JsonNode.Parse(d.Get(F.Card) ?? "{}") }; })
            .OrderBy(h => h.Card?["name"]?.ToString()).ToList();
    });

    /// <summary>Text of one page, highlighted, for the preview pane.</summary>
    public (int page, int pages, string html)? PageText(long id, int page, string q) => _ix.WithPages(ps =>
    {
        var pq = QueryParser.Parse(q, _rules);
        var terms = Highlight.Terms(pq, _rules);
        var top = ps.Search(new TermQuery(new Term(F.PItem, id.ToString())), null, 10000, new Sort(new SortField(F.PNum, SortFieldType.INT64)));
        if (top.ScoreDocs.Length == 0) return ((int page, int pages, string html)?)null;
        var docs = top.ScoreDocs.Select(sd => ps.Doc(sd.Doc)).ToList();
        var d = docs.FirstOrDefault(x => x.GetField(F.PNum)?.GetInt32Value() == page) ?? docs[0];
        int n = d.GetField(F.PNum)?.GetInt32Value() ?? 1;
        return (n, docs.Count, Highlight.Full(d.Get(F.PText) ?? "", terms));
    });

    /// <summary>Pages that match the query in one item, best first (for "jump to next match").</summary>
    public List<int> MatchingPages(long id, string q) => _ix.WithPages(ps =>
    {
        var pq = PageQuery(QueryParser.Parse(q, _rules));
        if (pq == null) return new List<int>();
        var top = ps.Search(pq, new QueryWrapperFilter(new TermQuery(new Term(F.PItem, id.ToString()))), 200);
        return top.ScoreDocs.Select(sd => ps.Doc(sd.Doc).GetField(F.PNum)?.GetInt32Value() ?? 0).Where(n => n > 0).ToList();
    });

    // ---------- map ----------

    public sealed record MapPoint(double Lat, double Lon, int N, string? Key, long? Id, double[]? Bbox);

    /// <summary>
    /// Every matching report as a point (one per report), merged into grid clusters at the client's zoom
    /// when there are many. Uses doc values only, so it stays fast for hundreds of thousands of matches.
    /// </summary>
    public (List<MapPoint> points, long located, long total) Map(SearchRequest req, Principal who, int zoom)
    {
        var q = Prepare(req);
        var text = TextQuery(q, strict: true) ?? new MatchAllDocsQuery();
        var filter = FilterQuery(q, who);
        return _ix.WithDocs(s =>
        {
            var c = new GeoCollector();
            s.Search(text, new QueryWrapperFilter(filter), c);
            var reports = c.Reports;
            double cell = 360.0 / (256 * Math.Pow(2, Math.Clamp(zoom, 0, 20))) * 36;   // ~36 px cells
            var pts = new List<MapPoint>();
            if (reports.Count <= 1500 || zoom >= 13)
                pts = reports.Values.Select(r => new MapPoint(Math.Round(r.lat, 5), Math.Round(r.lon, 5), 1, r.key, r.id, null)).ToList();
            else
            {
                foreach (var g in reports.Values.GroupBy(r => (Math.Floor(r.lat / cell), Math.Floor(r.lon / cell))))
                {
                    int n = g.Count();
                    var one = g.First();
                    pts.Add(n == 1 ? new MapPoint(Math.Round(one.lat, 5), Math.Round(one.lon, 5), 1, one.key, one.id, null)
                        : new MapPoint(Math.Round(g.Average(r => r.lat), 5), Math.Round(g.Average(r => r.lon), 5), n, null, null,
                            [g.Min(r => r.lon), g.Min(r => r.lat), g.Max(r => r.lon), g.Max(r => r.lat)]));
                }
            }
            return (pts, (long)reports.Count, c.Total);
        });
    }

    sealed class GeoCollector : ICollector
    {
        public readonly Dictionary<string, (double lat, double lon, string key, long id)> Reports = new();
        public long Total;
        NumericDocValues? _lat, _lon, _id;
        SortedDocValues? _coll;
        readonly BytesRef _br = new();
        public void SetScorer(Scorer scorer) { }
        public void SetNextReader(AtomicReaderContext ctx)
        {
            var r = ctx.AtomicReader;
            _lat = r.GetNumericDocValues(F.Lat); _lon = r.GetNumericDocValues(F.Lon); _id = r.GetNumericDocValues(F.IdV); _coll = r.GetSortedDocValues(F.CollV);
        }
        public void Collect(int doc)
        {
            Total++;
            if (_lat == null || _lon == null) return;
            double lat = BitConverter.Int64BitsToDouble(_lat.Get(doc)), lon = BitConverter.Int64BitsToDouble(_lon.Get(doc));
            if (double.IsNaN(lat) || double.IsNaN(lon)) return;
            string key;
            if (_coll != null) { _coll.Get(doc, _br); key = _br.Utf8ToString(); } else key = doc.ToString();
            if (!Reports.ContainsKey(key)) Reports[key] = (lat, lon, key, _id?.Get(doc) ?? 0);
        }
        public bool AcceptsDocsOutOfOrder => true;
    }

    // ---------- suggestions ----------

    public List<object> Suggest(string prefix, Principal who, int n = 8)
    {
        var res = new List<object>();
        prefix = prefix.Trim();
        if (prefix.Length < 2) return res;
        var lower = prefix.ToLowerInvariant();
        _ix.WithDocs(s =>
        {
            var state = _ix.FacetState(s.IndexReader);
            if (state == null) return 0;
            // Facet values that start with (or, for tenements and companies, contain) what was typed.
            var dv = state.GetDocValues();
            foreach (var (dim, label) in new[] { (F.DTenement, "Tenement"), (F.DCommodity, "Commodity"), (F.DCompany, "Company"), (F.DLith, "Rock type"), (F.DMethod, "Method"), (F.DRType, "Report type"), (F.DAsx, "ASX code") })
            {
                if (!state.PrefixToOrdRange.TryGetValue(dim, out var range)) continue;
                var br = new BytesRef();
                int found = 0;
                for (int ord = range.Start; ord <= range.End && found < 4; ord++)
                {
                    dv.LookupOrd(ord, br);
                    var full = br.Utf8ToString();
                    var val = full[(full.IndexOf('\u001f') + 1)..];
                    var vl = val.ToLowerInvariant();
                    bool hit = vl.StartsWith(lower) || ((dim == F.DCompany || dim == F.DTenement) && lower.Length >= 3 && vl.Contains(lower)) ||
                               (dim == F.DTenement && vl.Replace(" ", "").StartsWith(lower.Replace(" ", "")));
                    if (!hit) continue;
                    res.Add(new { kind = "facet", dim, label, value = val });
                    found++;
                }
            }
            // Report titles.
            var words = SiloAnalyzer.Tokens(prefix);
            if (words.Count > 0)
            {
                var bq = new BooleanQuery(true);
                for (int i = 0; i < words.Count; i++)
                    bq.Add(i == words.Count - 1 ? new PrefixQuery(new Term(F.Title, words[i])) : new TermQuery(new Term(F.Title, words[i])), Occur.MUST);
                var top = s.Search(bq, new QueryWrapperFilter(FilterQuery(new ParsedQuery(), who)), 40);
                var seen = new HashSet<string>();
                foreach (var sd in top.ScoreDocs)
                {
                    var card = JsonNode.Parse(s.Doc(sd.Doc, new HashSet<string> { F.Card }).Get(F.Card) ?? "{}");
                    var t = card?["title"]?.ToString();
                    if (t == null || !seen.Add(t)) continue;
                    res.Add(new { kind = "title", value = t, coll = card?["coll"]?.ToString(), ds = card?["ds"]?.ToString(), year = card?["year"]?.ToString() });
                    if (seen.Count >= 5) break;
                }
            }
            return 0;
        });
        return res.Take(n + 4).ToList();
    }
}

/// <summary>Builds highlighted snippets from page text.</summary>
public static class Highlight
{
    public sealed record TermSet(HashSet<string> Exact, HashSet<string> Stems, List<List<string>> Phrases);

    public static TermSet Terms(ParsedQuery q, Rules rules)
    {
        var exact = new HashSet<string>(); var stems = new HashSet<string>(); var phrases = new List<List<string>>();
        foreach (var t in q.Clauses.SelectMany(c => c).Concat(q.Tenements))
        {
            var toks = SiloAnalyzer.Tokens(t.Trim('"'));
            if (toks.Count > 1) phrases.Add(toks);
            foreach (var x in toks) { exact.Add(x); foreach (var s in SiloAnalyzer.Tokens(x, true)) stems.Add(s); }
            if (!t.StartsWith('"') && rules.SynonymMap.TryGetValue(t, out var syn))
                foreach (var s in syn) { var st = SiloAnalyzer.Tokens(s); if (st.Count == 1) exact.Add(st[0]); else phrases.Add(st); }
        }
        return new TermSet(exact, stems, phrases);
    }

    static bool[] Marks(List<(string term, int start, int end)> toks, TermSet t)
    {
        var mark = new bool[toks.Count];
        for (int i = 0; i < toks.Count; i++)
        {
            var w = toks[i].term;
            if (t.Exact.Contains(w)) { mark[i] = true; continue; }
            if (t.Stems.Count > 0 && w.Length > 3 && SiloAnalyzer.Tokens(w, true) is [var st] && t.Stems.Contains(st)) mark[i] = true;
        }
        // Phrases count only where the whole phrase is there.
        foreach (var ph in t.Phrases)
            for (int i = 0; i + ph.Count <= toks.Count; i++)
            {
                bool all = true;
                for (int k = 0; k < ph.Count && all; k++) all = toks[i + k].term == ph[k];
                if (all) for (int k = 0; k < ph.Count; k++) mark[i + k] = true;
            }
        return mark;
    }

    /// <summary>The best ~260 characters of a page: the window with the most distinct query words.</summary>
    public static string Snippet(string text, TermSet t, int len = 260)
    {
        if (text.Length == 0) return "";
        var scan = text.Length > 200_000 ? text[..200_000] : text;
        var toks = SiloAnalyzer.TokensWithOffsets(scan);
        var mark = Marks(toks, t);
        int bestStart = 0; double bestScore = -1;
        int win = 40;
        for (int i = 0; i < toks.Count; i++)
        {
            if (!mark[i]) continue;
            var distinct = new HashSet<string>(); int hits = 0;
            for (int k = i; k < Math.Min(toks.Count, i + win); k++) if (mark[k]) { distinct.Add(toks[k].term); hits++; }
            double score = distinct.Count * 10 + hits;
            if (score > bestScore) { bestScore = score; bestStart = i; }
        }
        int from = toks.Count == 0 ? 0 : Math.Max(0, toks[Math.Max(0, bestStart - 6)].start);
        if (bestScore < 0) from = 0;
        int to = Math.Min(scan.Length, from + len);
        // Snap to word edges.
        while (to < scan.Length && to - from < len + 25 && char.IsLetterOrDigit(scan[to])) to++;
        var sb = new StringBuilder();
        if (from > 0) sb.Append("… ");
        int pos = from;
        for (int i = 0; i < toks.Count; i++)
        {
            var (_, s, e) = toks[i];
            if (e <= from) continue;
            if (s >= to) break;
            if (!mark[i]) continue;
            sb.Append(Enc(scan[pos..s])).Append("<mark>").Append(Enc(scan[s..e])).Append("</mark>");
            pos = e;
        }
        if (pos < to) sb.Append(Enc(scan[pos..to]));
        if (to < scan.Length) sb.Append(" …");
        return sb.ToString();
    }

    /// <summary>A whole page with every query word marked (preview pane).</summary>
    public static string Full(string text, TermSet t)
    {
        if (text.Length > 400_000) text = text[..400_000];
        var toks = SiloAnalyzer.TokensWithOffsets(text);
        var mark = Marks(toks, t);
        var sb = new StringBuilder(text.Length + 256);
        int pos = 0;
        for (int i = 0; i < toks.Count; i++)
        {
            if (!mark[i]) continue;
            var (_, s, e) = toks[i];
            sb.Append(Enc(text[pos..s])).Append("<mark>").Append(Enc(text[s..e])).Append("</mark>");
            pos = e;
        }
        sb.Append(Enc(text[pos..]));
        return sb.ToString();
    }

    static string Enc(string s) => WebUtility.HtmlEncode(s);
}
