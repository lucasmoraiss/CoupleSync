using System.Text;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "PdfExtraction")]
public sealed class PdfPigTextExtractorTests
{
    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractsText_FromTextBasedPdf_ReturnsNonEmptyString()
    {
        using var stream = BuildMinimalTextPdf("Hello World from CoupleSync bank statement extractor");
        var extractor = new PdfPigTextExtractor();

        var result = extractor.ExtractText(stream);

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("Hello", result);
    }

    // ── Encrypted PDF ─────────────────────────────────────────────────────────

    [Fact]
    public void ThrowsOcrException_WhenPdfIsEncrypted()
    {
        var extractor = new ThrowEncryptedExtractor();

        var ex = Assert.Throws<OcrException>(() =>
            extractor.ExtractText(new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 })));

        Assert.Equal("PDF_ENCRYPTED", ex.Code);
    }

    // ── Degenerate text (< 50 chars) returned as-is ───────────────────────────

    [Fact]
    public void ExtractsText_ShortContent_ReturnsAsIs()
    {
        using var stream = BuildMinimalTextPdf("Hi");
        var extractor = new PdfPigTextExtractor();

        var result = extractor.ExtractText(stream);

        // Caller decides on EC-002; extractor just returns whatever it found
        Assert.NotNull(result);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    /// <summary>Overrides OpenDocument to simulate an encrypted PDF.</summary>
    private sealed class ThrowEncryptedExtractor : PdfPigTextExtractor
    {
        protected override PdfDocument OpenDocument(Stream stream)
            => throw new PdfDocumentEncryptedException("Test encrypted PDF");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal valid single-page PDF with the given text in the content stream.
    /// Uses Latin-1 so that string.Length == byte count (required for /Length accuracy).
    /// </summary>
    private static MemoryStream BuildMinimalTextPdf(string textLine)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();

        sb.Append("%PDF-1.4\n");

        // Object 1: Catalog
        offsets.Add(sb.Length);
        sb.Append("1 0 obj\n<</Type /Catalog /Pages 2 0 R>>\nendobj\n");

        // Object 2: Pages
        offsets.Add(sb.Length);
        sb.Append("2 0 obj\n<</Type /Pages /Kids [3 0 R] /Count 1>>\nendobj\n");

        // Object 3: Page
        offsets.Add(sb.Length);
        sb.Append("3 0 obj\n<</Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources <</Font <</F1 5 0 R>>>>>>\nendobj\n");

        // Object 4: Content stream with text
        string streamText = $"BT /F1 12 Tf 50 100 Td ({textLine}) Tj ET\n";
        offsets.Add(sb.Length);
        sb.Append($"4 0 obj\n<</Length {streamText.Length}>>\nstream\n{streamText}endstream\nendobj\n");

        // Object 5: Font
        offsets.Add(sb.Length);
        sb.Append("5 0 obj\n<</Type /Font /Subtype /Type1 /BaseFont /Helvetica>>\nendobj\n");

        int xrefOffset = sb.Length;
        sb.Append("xref\n");
        sb.Append("0 6\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");

        sb.Append($"trailer\n<</Size 6 /Root 1 0 R>>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString()));
    }
}
