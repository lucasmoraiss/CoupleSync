using System.Text.Json;
using System.Text.Json.Serialization;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser;

/// <summary>
/// IOcrProvider implementation that uses PdfPig for text extraction and
/// bank-specific regex parsers instead of Azure Document Intelligence.
/// Controlled by the USE_LOCAL_PDF_PARSER feature flag (default: true).
/// </summary>
public sealed class LocalPdfParserProvider : IOcrProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IStorageAdapter _storage;
    private readonly IPdfTextExtractor _pdfExtractor;
    private readonly BankDetector _bankDetector;
    private readonly ILogger<LocalPdfParserProvider> _logger;

    public LocalPdfParserProvider(
        IStorageAdapter storage,
        IPdfTextExtractor pdfExtractor,
        BankDetector bankDetector,
        ILogger<LocalPdfParserProvider> logger)
    {
        _storage = storage;
        _pdfExtractor = pdfExtractor;
        _bankDetector = bankDetector;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string> AnalyzeAsync(string storagePath, string mimeType, CancellationToken ct)
    {
        // 0. Reject image uploads — PdfPig cannot process JPEG/PNG (FR-002)
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new OcrException(
                "IMAGE_NOT_SUPPORTED",
                "Processamento de imagens ainda não está disponível. Envie um extrato em PDF digital.");

        // 1. Load stream from storage
        await using var stream = await _storage.DownloadAsync(storagePath, ct);

        // 2. Extract text via PdfPig — OcrException(PDF_ENCRYPTED) propagates as-is (EC-001)
        var text = _pdfExtractor.ExtractText(stream);

        // 3. Reject scanned/image-only PDFs (text too short to contain real statement data)
        if (text.Length < 50)
            throw new OcrException(
                "PDF_TOO_SHORT",
                "O PDF não contém texto suficiente. Por favor, use um extrato digital (não escaneado).");

        // 4. Detect bank — throws BankFormatUnknownException if none match (EC-003)
        var parser = _bankDetector.Detect(text);

        // 5. Parse transactions
        var transactions = parser.Parse(text);

        // 6. No transactions found is treated as an unrecoverable parse failure (EC-003 variant)
        if (transactions.Count == 0)
            throw new OcrException(
                "NO_TRANSACTIONS_FOUND",
                "Nenhuma transação foi encontrada no extrato. Verifique se o período do extrato está correto.");

        // Security NFR-004: log only count and bank name — never raw text or transaction details
        _logger.LogInformation(
            "LocalPdfParser: detected {Bank}, parsed {Count} transaction(s) from {Path}",
            parser.BankName, transactions.Count, storagePath);

        // 7. Serialize with discriminator so OcrProcessingService can route to the local-pdf branch
        var result = new LocalPdfResult(
            Provider: "local-pdf",
            BankName: parser.BankName,
            Transactions: transactions
                .Select(t => new LocalPdfTransaction(
                    Date: t.Date.ToString("yyyy-MM-dd"),
                    Description: t.Description,
                    Amount: t.Amount,
                    Type: t.Type.ToString()))
                .ToArray());

        return JsonSerializer.Serialize(result, SerializerOptions);
    }
}

// ─── Private DTO records (not part of the public API) ───────────────────────

internal sealed record LocalPdfResult(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("bankName")] string BankName,
    [property: JsonPropertyName("transactions")] LocalPdfTransaction[] Transactions);

internal sealed record LocalPdfTransaction(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("type")] string Type);
