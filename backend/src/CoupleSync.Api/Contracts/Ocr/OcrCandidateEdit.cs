namespace CoupleSync.Api.Contracts.Ocr;

/// <summary>
/// Correction made by the user on the review screen for one selected statement line.
/// Only the fields that changed are sent; omitted fields keep the value read from the statement.
/// </summary>
public sealed record OcrCandidateEdit(int Index, string? Description = null, decimal? Amount = null);
