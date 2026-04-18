namespace CoupleSync.Domain.ValueObjects;

public sealed record ParsedBankStatement(
    string BankName,
    IReadOnlyList<ParsedTransaction> Transactions);
