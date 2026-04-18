namespace CoupleSync.Domain.ValueObjects;

public enum TransactionType
{
    Credit,
    Debit
}

public sealed record ParsedTransaction(
    DateTime Date,
    string Description,
    decimal Amount,
    TransactionType Type);
