namespace CoupleSync.Api.Contracts.Ocr;

/// <param name="Candidates">Debit lines that can be confirmed (credits are never in this list).</param>
/// <param name="Credits">Credit lines (entradas) found in the statement. Informational: they are not importable.</param>
/// <param name="CreditsCount">How many credit lines were not imported ("3 entradas não importadas").</param>
public sealed record OcrResultsResponse(
    IReadOnlyList<OcrCandidateResponse> Candidates,
    IReadOnlyList<OcrCreditResponse>? Credits = null,
    int CreditsCount = 0);
