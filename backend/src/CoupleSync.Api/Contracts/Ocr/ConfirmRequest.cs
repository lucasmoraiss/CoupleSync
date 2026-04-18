namespace CoupleSync.Api.Contracts.Ocr;

public sealed record ConfirmRequest(
    IReadOnlyList<int> SelectedIndices,
    IReadOnlyList<OcrCategoryOverride>? CategoryOverrides = null);
