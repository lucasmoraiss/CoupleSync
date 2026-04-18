using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "LocalPdfParser")]
public sealed class LocalPdfParserProviderTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LocalPdfParserProvider BuildProvider(
        IStorageAdapter? storage = null,
        IPdfTextExtractor? extractor = null,
        BankDetector? detector = null)
    {
        return new LocalPdfParserProvider(
            storage ?? new FakeStorage(new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 })),
            extractor ?? new FakeExtractor("some extracted pdf text that is long enough to pass validation"),
            detector ?? new BankDetector([new FakeParser("TestBank", canParse: true, transactions:
            [
                new ParsedTransaction(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), "PIX Recebido", 1500.00m, TransactionType.Credit)
            ])]),
            NullLogger<LocalPdfParserProvider>.Instance);
    }

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_ReturnsJsonWithLocalPdfDiscriminator_OnSuccess()
    {
        var provider = BuildProvider();

        var json = await provider.AnalyzeAsync("uploads/couple1/file1.pdf", "application/pdf", CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("local-pdf", doc.RootElement.GetProperty("provider").GetString());
        Assert.Equal("TestBank", doc.RootElement.GetProperty("bankName").GetString());

        var txArray = doc.RootElement.GetProperty("transactions");
        Assert.Equal(1, txArray.GetArrayLength());
        Assert.Equal("PIX Recebido", txArray[0].GetProperty("description").GetString());
        Assert.Equal(1500.00m, txArray[0].GetProperty("amount").GetDecimal());
        Assert.Equal("Credit", txArray[0].GetProperty("type").GetString());
    }

    // ── EC-002: text too short ────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_ThrowsOcrException_WhenTextTooShort()
    {
        var provider = BuildProvider(
            extractor: new FakeExtractor("short"));

        var ex = await Assert.ThrowsAsync<OcrException>(() =>
            provider.AnalyzeAsync("uploads/couple1/file1.pdf", "application/pdf", CancellationToken.None));

        Assert.Equal("PDF_TOO_SHORT", ex.Code);
    }

    [Fact]
    public async Task AnalyzeAsync_ThrowsOcrException_WhenTextIsAtBoundary_49Chars()
    {
        var provider = BuildProvider(
            extractor: new FakeExtractor(new string('x', 49)));

        var ex = await Assert.ThrowsAsync<OcrException>(() =>
            provider.AnalyzeAsync("uploads/couple1/file1.pdf", "application/pdf", CancellationToken.None));

        Assert.Equal("PDF_TOO_SHORT", ex.Code);
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotThrow_WhenTextIsExactly50Chars()
    {
        var provider = BuildProvider(
            extractor: new FakeExtractor(new string('x', 50)),
            detector: new BankDetector([new FakeParser("X", canParse: false)]));

        // Should not throw PDF_TOO_SHORT; BankFormatUnknownException expected (no text match)
        await Assert.ThrowsAsync<BankFormatUnknownException>(() =>
            provider.AnalyzeAsync("uploads/couple1/file1.pdf", "application/pdf", CancellationToken.None));
    }

    // ── EC-003: bank not detected ─────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_ThrowsBankFormatUnknownException_WhenBankNotDetected()
    {
        var provider = BuildProvider(
            extractor: new FakeExtractor("enough text content here that is at least fifty characters long to pass check"),
            detector: new BankDetector([new FakeParser("X", canParse: false)]));

        await Assert.ThrowsAsync<BankFormatUnknownException>(() =>
            provider.AnalyzeAsync("uploads/couple1/file1.pdf", "application/pdf", CancellationToken.None));
    }

    // ── EC-003 variant: zero transactions ────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_ThrowsOcrException_WhenParserReturnsNoTransactions()
    {
        var provider = BuildProvider(
            extractor: new FakeExtractor("enough text content here that is at least fifty characters long to pass check"),
            detector: new BankDetector([new FakeParser("EmptyBank", canParse: true, transactions: [])]));

        var ex = await Assert.ThrowsAsync<OcrException>(() =>
            provider.AnalyzeAsync("uploads/couple1/file1.pdf", "application/pdf", CancellationToken.None));

        Assert.Equal("NO_TRANSACTIONS_FOUND", ex.Code);
    }

    // ── EC-001: PDF_ENCRYPTED re-thrown ──────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_PropagatesOcrException_WhenPdfEncrypted()
    {
        var provider = BuildProvider(
            extractor: new ThrowingExtractor(new OcrException("PDF_ENCRYPTED", "Encrypted.")));

        var ex = await Assert.ThrowsAsync<OcrException>(() =>
            provider.AnalyzeAsync("uploads/couple1/file1.pdf", "application/pdf", CancellationToken.None));

        Assert.Equal("PDF_ENCRYPTED", ex.Code);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeStorage : IStorageAdapter
    {
        private readonly Stream _stream;
        public FakeStorage(Stream stream) => _stream = stream;

        public Task<string> UploadAsync(Guid coupleId, Guid uploadId, Stream content, string mimeType, CancellationToken ct)
            => Task.FromResult(string.Empty);

        public Task<Stream> DownloadAsync(string storagePath, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream());
    }

    private sealed class FakeExtractor : IPdfTextExtractor
    {
        private readonly string _text;
        public FakeExtractor(string text) => _text = text;
        public string ExtractText(Stream pdfStream) => _text;
    }

    private sealed class ThrowingExtractor : IPdfTextExtractor
    {
        private readonly Exception _ex;
        public ThrowingExtractor(Exception ex) => _ex = ex;
        public string ExtractText(Stream pdfStream) => throw _ex;
    }

    private sealed class FakeParser : IBankStatementParser
    {
        private readonly bool _canParse;
        private readonly IReadOnlyList<ParsedTransaction> _transactions;

        public FakeParser(string bankName, bool canParse, IReadOnlyList<ParsedTransaction>? transactions = null)
        {
            BankName = bankName;
            _canParse = canParse;
            _transactions = transactions ?? [];
        }

        public string BankName { get; }
        public bool CanParse(string extractedText) => _canParse;
        public IReadOnlyList<ParsedTransaction> Parse(string extractedText) => _transactions;
    }
}
