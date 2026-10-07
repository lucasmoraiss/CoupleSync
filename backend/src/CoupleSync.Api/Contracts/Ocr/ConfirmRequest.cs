namespace CoupleSync.Api.Contracts.Ocr;

/// <param name="KeepJobOpen">
/// When true the lines that were neither selected nor discarded stay pending and can be confirmed later.
/// When omitted/false (what the installed app sends) the import closes after this call.
/// </param>
/// <param name="DiscardedIndices">Lines the user dropped for good; they no longer keep the import open.</param>
public sealed record ConfirmRequest(
    IReadOnlyList<int> SelectedIndices,
    IReadOnlyList<OcrCategoryOverride>? CategoryOverrides = null,
    IReadOnlyList<OcrCandidateEdit>? CandidateEdits = null,
    bool KeepJobOpen = false,
    IReadOnlyList<int>? DiscardedIndices = null);
