namespace CoupleSync.Application.Transactions.Commands;

public sealed record UpdateTransactionCategoryCommand(
    Guid TransactionId,
    Guid CoupleId,
    string Category);
