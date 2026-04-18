using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser;

public sealed class BankDetector
{
    private readonly IEnumerable<IBankStatementParser> _parsers;

    public BankDetector(IEnumerable<IBankStatementParser> parsers)
    {
        _parsers = parsers;
    }

    /// <summary>
    /// Returns the first registered parser that can handle the given extracted text.
    /// </summary>
    /// <exception cref="BankFormatUnknownException">Thrown when no parser matches (EC-003).</exception>
    public IBankStatementParser Detect(string extractedText)
    {
        return _parsers.FirstOrDefault(p => p.CanParse(extractedText))
            ?? throw new BankFormatUnknownException();
    }
}
