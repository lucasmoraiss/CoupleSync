namespace CoupleSync.Api.Contracts.Ocr;

public sealed record OcrStatusResponse(string Status, string? ErrorCode, DateTime? QuotaResetDate);
