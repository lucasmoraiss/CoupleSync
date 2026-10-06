namespace CoupleSync.Api.Contracts.Ocr;

/// <param name="TransactionsCreated">Transactions stored by this confirmation.</param>
/// <param name="DuplicatesSkipped">Selected lines ignored because the same statement line was already imported.</param>
public sealed record ConfirmResponse(int TransactionsCreated, int DuplicatesSkipped = 0);
