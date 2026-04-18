using System.Text.RegularExpressions;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

/// <summary>
/// Parses Itaú Unibanco PDF bank statements.
/// Statement format: DD/MM   Description   XX.XXX,XX-  (trailing minus = Debit, no minus = Credit)
/// Date has no year — assume current year; if parsed month > today's month, use previous year.
/// </summary>
public sealed class ItauParser : IBankStatementParser
{
    public string BankName => "Itaú";

    private static readonly Regex IdentifierPattern = new(
        @"Itaú Unibanco|itau\.com\.br|ITAÚ|Itau",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Captures: short date (DD/MM), description, amount with optional trailing minus
    private static readonly Regex TransactionPattern = new(
        @"(\d{2}/\d{2})\s+(.+?)\s+([\d\.]+,\d{2})(-?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public bool CanParse(string extractedText) => IdentifierPattern.IsMatch(extractedText);

    public IReadOnlyList<ParsedTransaction> Parse(string extractedText)
    {
        var transactions = new List<ParsedTransaction>();
        var today = DateTime.UtcNow;

        foreach (Match match in TransactionPattern.Matches(extractedText))
        {
            var shortDate = match.Groups[1].Value; // DD/MM
            var description = match.Groups[2].Value.Trim();
            var rawAmount = match.Groups[3].Value.Trim();
            var trailingMinus = match.Groups[4].Value.Trim();

            // Resolve year: if parsed month > current month, it's the previous year
            if (!DateTime.TryParseExact(shortDate, "dd/MM",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var partialDate))
                continue;

            var year = partialDate.Month > today.Month ? today.Year - 1 : today.Year;

            // Guard against leap-year edge cases (e.g. Feb 29 in non-leap years)
            DateTime date;
            try
            {
                date = new DateTime(year, partialDate.Month, partialDate.Day);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            if (!BrazilianCurrencyParser.TryParseAbsolute(rawAmount, out var amount))
                continue;

            // Trailing minus in Itaú format means Debit
            var type = trailingMinus == "-" ? TransactionType.Debit : TransactionType.Credit;
            transactions.Add(new ParsedTransaction(date, description, amount, type));
        }

        return transactions;
    }
}
