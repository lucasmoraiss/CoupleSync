namespace CoupleSync.Api.Contracts.Ocr;

/// <param name="LineState">Pending, Confirmed or Discarded: what already happened to this line of the review.</param>
public sealed record OcrCandidateResponse(
    int Index,
    DateTime Date,
    string Description,
    decimal Amount,
    string Currency,
    double Confidence,
    bool DuplicateSuspected,
    string? SuggestedCategory,
    string LineState = "Pending");
