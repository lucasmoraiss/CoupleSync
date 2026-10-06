namespace CoupleSync.Api.Contracts.Ocr;

/// <summary>An income line of the statement, shown in the review but never imported.</summary>
public sealed record OcrCreditResponse(
    DateTime Date,
    string Description,
    decimal Amount,
    string Currency);
