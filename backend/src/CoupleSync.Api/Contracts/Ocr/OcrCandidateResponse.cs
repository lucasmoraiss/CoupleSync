namespace CoupleSync.Api.Contracts.Ocr;

public sealed record OcrCandidateResponse(
    int Index,
    DateTime Date,
    string Description,
    decimal Amount,
    string Currency,
    double Confidence,
    bool DuplicateSuspected,
    string? SuggestedCategory);
