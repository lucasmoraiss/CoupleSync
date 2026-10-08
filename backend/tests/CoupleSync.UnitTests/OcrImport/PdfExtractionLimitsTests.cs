using System.Text;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Worker;

namespace CoupleSync.UnitTests.OcrImport;

/// <summary>A PDF with too many pages, or one that takes too long to read, fails with a clear error.</summary>
[Trait("Category", "PdfExtraction")]
public sealed class PdfExtractionLimitsTests
{
    [Fact]
    public void TheInProcessReader_KeepsItsLimitsOf50PagesAnd30Seconds_TheChildHasItsOwnInPdfWorkerOptions()
    {
        Assert.Equal(50, PdfPigTextExtractor.DefaultMaxPages);
        Assert.Equal(TimeSpan.FromSeconds(30), PdfPigTextExtractor.DefaultTimeout);
    }

    [Fact]
    public void ADocumentWithMorePagesThanTheLimit_FailsWithPdfTooManyPages()
    {
        using var stream = BuildPdf(pageCount: 51);

        var ex = Assert.Throws<OcrException>(() => new ChildProcessPdfTextExtractor(new PdfWorkerOptions()).ExtractText(stream));

        Assert.Equal("PDF_TOO_MANY_PAGES", ex.Code);
        Assert.Contains("50", ex.Message);
    }

    [Fact]
    public void ADocumentWithExactlyTheLimit_IsRead()
    {
        using var stream = BuildPdf(pageCount: 50);

        var text = new ChildProcessPdfTextExtractor(new PdfWorkerOptions()).ExtractText(stream);

        Assert.Contains("Pagina 1 do extrato", text);
        Assert.Contains("Pagina 50 do extrato", text);
    }

    [Fact]
    public void AReadThatNeverFinishes_FailsWithPdfTimeout_InsteadOfHangingTheCaller()
    {
        // A worker that never answers: an idle shell command (same on Windows and Linux).
        var options = ChildProcessPdfTextExtractorTests.IdleWorkerOptions() with { Timeout = TimeSpan.FromMilliseconds(500) };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var ex = Assert.Throws<OcrException>(() => new ChildProcessPdfTextExtractor(options).ExtractText(new MemoryStream([1, 2, 3])));

        Assert.Equal("PDF_TIMEOUT", ex.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the caller must not wait for the stuck read");
    }

    [Fact]
    public void ADocumentOverTheLimit_IsRejectedWithTheLimitOfTheOptions()
    {
        using var stream = BuildPdf(pageCount: 3);

        var ex = Assert.Throws<OcrException>(() => new ChildProcessPdfTextExtractor(new PdfWorkerOptions { MaxPages = 2 }).ExtractText(stream));

        Assert.Equal("PDF_TOO_MANY_PAGES", ex.Code);
        Assert.Contains("o limite é 2", ex.Message);
    }

    /// <summary>Minimal valid PDF with one text line per page (Latin-1 so string length equals byte count).</summary>
    private static MemoryStream BuildPdf(int pageCount)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();

        // Objects: 1 catalog, 2 pages, 3 font, then (page, content) pairs.
        offsets.Add(sb.Length);
        sb.Append("1 0 obj\n<</Type /Catalog /Pages 2 0 R>>\nendobj\n");

        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{4 + i * 2} 0 R"));
        offsets.Add(sb.Length);
        sb.Append($"2 0 obj\n<</Type /Pages /Kids [{kids}] /Count {pageCount}>>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("3 0 obj\n<</Type /Font /Subtype /Type1 /BaseFont /Helvetica>>\nendobj\n");

        for (var i = 0; i < pageCount; i++)
        {
            var pageObj = 4 + i * 2;
            var contentObj = pageObj + 1;
            var content = $"BT /F1 12 Tf 20 100 Td (Pagina {i + 1} do extrato) Tj ET\n";

            offsets.Add(sb.Length);
            sb.Append($"{pageObj} 0 obj\n<</Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents {contentObj} 0 R /Resources <</Font <</F1 3 0 R>>>>>>\nendobj\n");
            offsets.Add(sb.Length);
            sb.Append($"{contentObj} 0 obj\n<</Length {content.Length}>>\nstream\n{content}endstream\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<</Size {offsets.Count + 1} /Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");

        return new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString()));
    }
}
