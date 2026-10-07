namespace CoupleSync.Api.Contracts.Ocr;

/// <param name="FileName">Name of the file that was sent; null for imports created before it was stored.</param>
/// <param name="PendingLines">Debit lines not yet confirmed or discarded.</param>
public sealed record OcrOpenImportResponse(
    Guid UploadId,
    string? FileName,
    DateTime CreatedAtUtc,
    int PendingLines,
    int TotalLines,
    int CreditsCount);

public sealed record OcrOpenImportsResponse(IReadOnlyList<OcrOpenImportResponse> Imports);
