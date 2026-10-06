using System.Text.RegularExpressions;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

/// <summary>
/// Parses Nubank PDF bank statements.
/// Statement format: DD/MM/YYYY   Description   -R$ XX,XX  or  +R$ XX,XX
/// Negative amount → Debit; positive → Credit.
/// </summary>
public sealed class NubankParser : IBankStatementParser
{
    public string BankName => "Nubank";

    private static readonly Regex IdentifierPattern = new(
        @"Nu Pagamentos|nubank\.com\.br|NUBANK",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Captures: date (DD/MM/YYYY), description (non-greedy), amount (optional minus + optional R$ + digits).
    // PdfPig may hand a whole page over as a single line, with the next date glued to the previous amount
    // ("-R$ 58,2006/09/2026 ..."): so the description never runs into another date, and the amount ends at
    // whitespace, at the end of the line or right where the next date starts.
    private static readonly Regex TransactionPattern = new(
        @"(\d{2}/\d{2}/\d{4})\s+((?:(?!\d{2}/\d{2}/\d{4}).)+?)\s+([+-]?\s*R?\$?\s*[\d\.]+,\d{2})(?=\s|$|\d{2}/\d{2}/\d{4})",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public bool CanParse(string extractedText) => IdentifierPattern.IsMatch(extractedText);

    public IReadOnlyList<ParsedTransaction> Parse(string extractedText)
    {
        var transactions = new List<ParsedTransaction>();

        foreach (Match match in TransactionPattern.Matches(extractedText))
        {
            if (!DateTime.TryParseExact(match.Groups[1].Value, "dd/MM/yyyy",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date))
                continue;

            var description = match.Groups[2].Value.Trim();
            var rawAmount = match.Groups[3].Value.Trim();

            if (!BrazilianCurrencyParser.TryParse(rawAmount, out var amount))
                continue;

            var type = amount >= 0 ? TransactionType.Credit : TransactionType.Debit;
            transactions.Add(new ParsedTransaction(date, description, Math.Abs(amount), type));
        }

        return transactions;
    }
}
