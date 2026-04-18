namespace CoupleSync.Domain.Interfaces;

public interface IOcrProvider
{
    /// <summary>
    /// Sends the document at the given storage path to OCR and returns the raw JSON result.
    /// Throws OcrQuotaExhaustedException on 429.
    /// </summary>
    Task<string> AnalyzeAsync(string storagePath, string mimeType, CancellationToken ct);
}
