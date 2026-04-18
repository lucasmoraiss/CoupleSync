using System.Text.RegularExpressions;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

/// <summary>
/// Parses Banco Mercantil do Brasil PDF bank statements.
/// Statement format: DD/MM/YYYY   Description   XX.XXX,XX   D/C
/// D = Debit, C = Credit.
/// </summary>
public sealed class MercantilParser : IBankStatementParser
{
    public string BankName => "Mercantil";

    private static readonly Regex IdentifierPattern = new(
        @"Banco Mercantil|mercantil\.com\.br|MERCANTIL",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Captures: date, description (non-greedy), amount, D/C indicator
    private static readonly Regex TransactionPattern = new(
        @"(\d{2}/\d{2}/\d{4})\s+(.+?)\s+([\d\.]+,\d{2})\s+([DC])\b",
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
            var indicator = match.Groups[4].Value.Trim();

            if (!BrazilianCurrencyParser.TryParseAbsolute(rawAmount, out var amount))
                continue;

            var type = indicator == "C" ? TransactionType.Credit : TransactionType.Debit;
            transactions.Add(new ParsedTransaction(date, description, amount, type));
        }

        return transactions;
    }
}
