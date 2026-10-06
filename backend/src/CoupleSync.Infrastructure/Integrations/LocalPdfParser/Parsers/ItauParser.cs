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

    // Entry start: a short date (DD/MM) followed by whitespace, not part of a full date, not the end of a range
    // in a header ("de 01/09 a 30/09") and not an installment ("Parcela 03/10"). PdfPig may hand a whole page
    // over as a single line with the next date glued to the previous amount ("61,30-09/09 ..."), so entries
    // are cut at the dates, not at line ends. The amount is the LAST money value of the entry (this layout
    // has no running-balance column); a trailing minus means Debit.
    private static readonly Regex StartPattern = new(
        @"(?<!\b(?:de|a|até|parcela|parc\.?)\s+)(?<![\d/])\d{2}/\d{2}(?![\d/])(?=\s)|(?<=,\d{2}-?)\d{2}/\d{2}(?![\d/])(?=\s)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MoneyPattern = new(
        @"\d[\d\.]*,\d{2}-?",
        RegexOptions.Compiled);

    public bool CanParse(string extractedText) => IdentifierPattern.IsMatch(extractedText);

    public IReadOnlyList<ParsedTransaction> Parse(string extractedText)
    {
        var transactions = new List<ParsedTransaction>();
        var today = DateTime.UtcNow;

        foreach (var segment in StatementSegments.Split(extractedText, StartPattern))
        {
            var shortDate = segment[..5]; // DD/MM
            var body = segment[5..];
            var moneyMatch = StatementSegments.LastMatch(MoneyPattern, body);
            if (moneyMatch is null)
                continue;

            var description = body[..moneyMatch.Index].Trim();
            if (description.Length == 0)
                continue;

            var trailingMinus = moneyMatch.Value.EndsWith('-') ? "-" : "";
            var rawAmount = moneyMatch.Value.TrimEnd('-');

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
