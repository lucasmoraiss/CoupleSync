using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Domain.Interfaces;

public interface IBankStatementParser
{
    string BankName { get; }

    bool CanParse(string extractedText);

    IReadOnlyList<ParsedTransaction> Parse(string extractedText);
}
