namespace CoupleSync.Api.Contracts.Ocr;

public sealed record OcrResultsResponse(IReadOnlyList<OcrCandidateResponse> Candidates);
