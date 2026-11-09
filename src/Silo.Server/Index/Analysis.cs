using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.En;
using Lucene.Net.Analysis.Miscellaneous;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Util;

namespace Silo.Server.Index;

/// <summary>
/// Letters and digits make tokens, and a token also breaks where letters meet digits, so codes written
/// different ways match: "E28/589", "E 28/589" and "e28-589" all read e · 28 · 589; "WRC001" reads wrc · 001.
/// </summary>
public sealed class SiloTokenizer : Tokenizer
{
    readonly ICharTermAttribute _term;
    readonly IOffsetAttribute _off;
    readonly char[] _buf = new char[8192];
    int _len, _pos, _base;
    bool _eof;

    public SiloTokenizer(TextReader input) : base(input)
    {
        _term = AddAttribute<ICharTermAttribute>();
        _off = AddAttribute<IOffsetAttribute>();
    }

    static int Kind(char c) => char.IsLetter(c) ? 1 : char.IsDigit(c) ? 2 : 0;

    bool Fill()
    {
        if (_eof) return false;
        // keep nothing: tokens are cut at buffer ends only when longer than the buffer (never in practice)
        _base += _len;
        _len = m_input.Read(_buf, 0, _buf.Length);
        _pos = 0;
        if (_len <= 0) { _len = 0; _eof = true; return false; }
        return true;
    }

    public override bool IncrementToken()
    {
        ClearAttributes();
        while (true)
        {
            if (_pos >= _len && !Fill()) return false;
            while (_pos < _len && Kind(_buf[_pos]) == 0) _pos++;
            if (_pos >= _len) continue;
            int start = _pos, k = Kind(_buf[_pos]);
            var sb = new System.Text.StringBuilder();
            int startAbs = _base + start;
            while (true)
            {
                while (_pos < _len && Kind(_buf[_pos]) == k && sb.Length < 255) sb.Append(_buf[_pos++]);
                if (_pos < _len || sb.Length >= 255) break;
                if (!Fill()) break;                        // token runs across the buffer edge: carry on
            }
            if (sb.Length == 0) continue;
            _term.SetEmpty().Append(sb.ToString());
            _off.SetOffset(CorrectOffset(startAbs), CorrectOffset(startAbs + sb.Length));
            return true;
        }
    }

    public override void End()
    {
        base.End();
        int final = CorrectOffset(_base + _len);
        _off.SetOffset(final, final);
    }

    public override void Reset()
    {
        base.Reset();
        _len = _pos = _base = 0; _eof = false;
    }
}

/// <summary>Lower-case, fold accents (and British/American spellings are handled by synonyms); optional English stemming.</summary>
public sealed class SiloAnalyzer : Analyzer
{
    readonly bool _stem;
    public SiloAnalyzer(bool stem) { _stem = stem; }
    protected override TokenStreamComponents CreateComponents(string fieldName, TextReader reader)
    {
        var tok = new SiloTokenizer(reader);
        TokenStream ts = new LowerCaseFilter(SearchIndex.V, tok);
        ts = new ASCIIFoldingFilter(ts);
        if (_stem) ts = new PorterStemFilter(ts);
        return new TokenStreamComponents(tok, ts);
    }

    public static readonly SiloAnalyzer Plain = new(false), Stem = new(true);

    public static List<string> Tokens(string text, bool stem = false)
    {
        var list = new List<string>();
        using var ts = (stem ? Stem : Plain).GetTokenStream("x", text);
        var term = ts.AddAttribute<ICharTermAttribute>();
        ts.Reset();
        while (ts.IncrementToken()) list.Add(term.ToString());
        ts.End();
        return list;
    }

    public static List<(string term, int start, int end)> TokensWithOffsets(string text)
    {
        var list = new List<(string, int, int)>();
        using var ts = Plain.GetTokenStream("x", text);
        var term = ts.AddAttribute<ICharTermAttribute>();
        var off = ts.AddAttribute<IOffsetAttribute>();
        ts.Reset();
        while (ts.IncrementToken()) list.Add((term.ToString(), off.StartOffset, off.EndOffset));
        ts.End();
        return list;
    }
}
