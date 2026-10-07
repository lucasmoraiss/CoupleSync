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

    // Entry start: a full date (DD/MM/YYYY) followed by whitespace. PdfPig may hand a whole page over as a
    // single line with the next date glued to the previous amount ("-R$ 58,2006/09/2026 ..."), so entries are
    // cut at the dates, not at line ends. The amount is the LAST money value of the entry.
    private static readonly Regex StartPattern = new(
        @"\d{2}/\d{2}/\d{4}(?=\s)",
        RegexOptions.Compiled);

    private static readonly Regex MoneyPattern = new(
        @"[+-]?\s*R?\$?\s*\d[\d\.]*,\d{2}",
        RegexOptions.Compiled);

    public bool CanParse(string extractedText) => IdentifierPattern.IsMatch(extractedText);

    public IReadOnlyList<ParsedTransaction> Parse(string extractedText)
    {
        var transactions = new List<ParsedTransaction>();

        foreach (var segment in StatementSegments.Split(extractedText, StartPattern))
        {
            if (!DateTime.TryParseExact(segment[..10], "dd/MM/yyyy",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date))
                continue;

            var body = segment[10..];
            var money = StatementSegments.LastMatch(MoneyPattern, body);
            if (money is null)
                continue;

            var description = body[..money.Index].Trim();
            if (description.Length == 0)
                continue;

            if (!BrazilianCurrencyParser.TryParse(money.Value.Trim(), out var amount))
                continue;

            var type = amount >= 0 ? TransactionType.Credit : TransactionType.Debit;
            transactions.Add(new ParsedTransaction(date, description, Math.Abs(amount), type));
        }

        return transactions;
    }
}
