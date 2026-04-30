namespace CoupleSync.Application.Transactions.Commands;

public sealed record DeleteTransactionCommand(
    Guid TransactionId,
    Guid UserId,
    Guid CoupleId);
