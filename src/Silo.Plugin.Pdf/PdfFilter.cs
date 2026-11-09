using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Silo.Plugins.Pdf;

/// <summary>
/// PDF text by page with PdfPig. Pages with no usable text layer are flagged for the core's OCR.
/// A text layer that is mostly junk (bad font encodings in old scans) is flagged too.
/// </summary>
public sealed class PdfFilter : FilterPlugin
{
    public override PluginManifest Manifest { get; } = new()
    {
        Id = "silo.filter.pdf",
        Name = "PDF documents",
        Version = "1.0.0",
        Kind = "filter",
        Description = "Text by page from PDF files; scanned pages are passed to OCR.",
        Publisher = "Silo",
        Extensions = [".pdf"],
        Concurrency = 16,
        TimeoutSec = 900,
        MaxFileMB = 1500,
        Fields =
        [
            new FieldSpec { Name = "pdf_producer", Label = "PDF producer", Type = "keyword", Facet = false },
        ],
    };

    static readonly Regex Hyphen = new(@"(\p{L})-\s*\n\s*(\p{Ll})", RegexOptions.Compiled);
    static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

    public override ExtractResult Extract(ExtractArgs a, CancellationToken ct)
    {
        var r = new ExtractResult();
        using var doc = PdfDocument.Open(a.Path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        r.TotalPages = doc.NumberOfPages;
        long chars = 0;
        for (int i = 1; i <= doc.NumberOfPages && i <= a.MaxPages; i++)
        {
            string text = "";
            try
            {
                var page = doc.GetPage(i);
                text = ContentOrderTextExtractor.GetText(page);
            }
            catch (Exception e) { if (r.Warnings.Count < 5) r.Warnings.Add($"page {i}: {e.Message}"); }
            text = Space.Replace(Hyphen.Replace(text, "$1$2"), " ").Trim();
            bool needsOcr = text.Length < 20 || Junk(text);
            if (chars + text.Length > a.MaxChars) { r.Warnings.Add($"text cut at page {i}"); break; }
            chars += text.Length;
            r.Pages.Add(new PageText { N = i, Text = text, NeedsOcr = needsOcr });
        }
        try
        {
            var info = doc.Information;
            if (!string.IsNullOrWhiteSpace(info.Title)) r.Title = info.Title.Trim();
            if (!string.IsNullOrWhiteSpace(info.Author)) r.Author = info.Author.Trim();
            if (!string.IsNullOrWhiteSpace(info.Producer)) r.Fields.Add("pdf_producer", info.Producer);
            if (info.GetCreatedDateTimeOffset() is { } c && c.Year > 1980 && c.Year <= DateTime.UtcNow.Year + 1) r.Created = c.UtcDateTime;
        }
        catch { }
        return r;
    }

    /// <summary>
    /// True when a page's text is mostly not words: a broken font encoding gives runs of symbols and
    /// consonant soup. Real text, even tables of numbers, passes.
    /// </summary>
    public static bool Junk(string text)
    {
        if (text.Length < 200) return false;
        int letters = 0, digits = 0, other = 0, vowels = 0;
        foreach (var ch in text)
        {
            if (char.IsLetter(ch)) { letters++; if ("aeiouAEIOU".IndexOf(ch) >= 0) vowels++; }
            else if (char.IsDigit(ch)) digits++;
            else if (!char.IsWhiteSpace(ch) && ".,;:()-/%'\"".IndexOf(ch) < 0) other++;
        }
        int total = letters + digits + other;
        if (total == 0) return true;
        if ((double)other / total > 0.35) return true;
        if (letters > 200 && (double)vowels / letters < 0.12) return true;
        return false;
    }
}
