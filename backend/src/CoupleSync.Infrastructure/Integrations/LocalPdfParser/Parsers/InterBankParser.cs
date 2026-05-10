using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

/// <summary>
/// Parses Banco Inter PDF bank statements (extrato) and credit card bills (fatura).
/// </summary>
public sealed class InterBankParser : IBankStatementParser
{
    public string BankName => "Inter";

    private static readonly Regex IdentifierPattern = new(
        @"Banco Inter|bancointer\.com\.br|BANCO INTER",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // O Regex agora tem um limitador rigoroso no Grupo 2:
    // (?:(?!\d{2}\s+de|\d{2}/\d{2}).){1,120}?
    // Significa: "Pegue até 120 caracteres, desde que NENHUM deles inicie uma nova data".
    private static readonly Regex JammedTransactionPattern = new(
        @"(\d{2}\s+de\s+[a-zA-Z]{3}\.?\s+\d{4}|\d{2}/\d{2}/\d{4}|\d{2}/\d{2})((?:(?!\d{2}\s+de|\d{2}/\d{2}).){1,120}?)([-+]*\s*R\$\s*[\d\.]+,\d{2}(?:\s+[DC])?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Dictionary<string, int> MonthMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1,
        ["fev"] = 2,
        ["mar"] = 3,
        ["abr"] = 4,
        ["mai"] = 5,
        ["jun"] = 6,
        ["jul"] = 7,
        ["ago"] = 8,
        ["set"] = 9,
        ["out"] = 10,
        ["nov"] = 11,
        ["dez"] = 12,
    };

    // GUARD RAILS: Palavras-chave que indicam que a linha é texto da fatura/boleto, e não transação.
    private static readonly string[] DescriptionBlacklist =
    {
        "Pagamento mínimo", "Limite de crédito", "Total da sua", "Fatura atual",
        "Despesas do", "Valor antecipado", "Data de Vencimento", "Encargos",
        "IOF", "Valor total", "Total a pagar", "Parcelamento", "Saldo",
        "Utilizado", "Disponível", "MORA / MULTA", "VALOR DO DOCUMENTO",
        "Precisa de uma força", "Confira as opções", "O IOF e juros",
        "DataMovimentaçãoBeneficiárioValor", "CARTÃO", "Próxima fatura",
        "Nº DOCUMENTO", "AUTENTICAÇÃO", "LOCAL DE PAGAMENTO", "AGÊNCIA / CEDENTE",
        // Expanded: credit card bill summary, total, and payment patterns
        "Resumo da fatura", "Total desta fatura", "Total da fatura",
        "Pagamento recebido", "Pgto débito automático", "Pagamento efetuado",
        "Crédito de pagamento", "Vencimento", "Fatura fechada",
        "Limite disponível", "Saldo anterior", "Resumo",
        "Pagamento em", "Pgto em", "Crédito em",
        "Total de compras", "Total de encargos",
        "Pagamento via", "Pix recebido"
    };

    public bool CanParse(string extractedText) => IdentifierPattern.IsMatch(extractedText);

    public IReadOnlyList<ParsedTransaction> Parse(string extractedText)
    {
        var transactions = new List<ParsedTransaction>();

        foreach (Match match in JammedTransactionPattern.Matches(extractedText))
        {
            var rawDate = match.Groups[1].Value.Trim();
            var rawDesc = match.Groups[2].Value.Trim();
            var rawAmount = match.Groups[3].Value.Trim();

            if (!TryParseAnyDate(rawDate, out var date))
                continue;

            // Limpa a descrição removendo hifens e sinais soltos deixados pela colagem do PDF
            var description = rawDesc.TrimEnd('-', '+', ' ').Trim();

            // ─── INÍCIO DOS GUARD RAILS ───

            // 1. Evita a falha onde a "Data de vencimento" cola direto no valor (ex: "20/04/2026R$ 677,87")
            // Nesse caso, o Regex acharia que a descrição é vazia. Bloqueamos descrições minúsculas.
            if (description.Length < 3)
                continue;

            // 2. Filtra qualquer sujeira textual baseada na nossa Blacklist
            if (DescriptionBlacklist.Any(badWord => description.Contains(badWord, StringComparison.OrdinalIgnoreCase)))
                continue;

            // ─── FIM DOS GUARD RAILS ───

            bool isCredit = rawAmount.Contains('+') || rawAmount.EndsWith("C", StringComparison.OrdinalIgnoreCase);
            var type = isCredit ? TransactionType.Credit : TransactionType.Debit;

            var cleanAmountStr = Regex.Replace(rawAmount, @"[^\d,]", "");

            if (!BrazilianCurrencyParser.TryParseAbsolute(cleanAmountStr, out var amount))
                continue;

            transactions.Add(new ParsedTransaction(date, description, amount, type));
        }

        return transactions;
    }

    private static bool TryParseAnyDate(string rawDate, out DateTime date)
    {
        date = default;
        rawDate = rawDate.ToLowerInvariant().Replace(".", "").Trim();

        if (DateTime.TryParseExact(rawDate, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;

        if (rawDate.Length == 5 && DateTime.TryParseExact(rawDate, "dd/MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            var today = DateTime.UtcNow;
            var year = date.Month > today.Month ? today.Year - 1 : today.Year;
            try { date = new DateTime(year, date.Month, date.Day); return true; }
            catch { return false; }
        }

        var faturaMatch = Regex.Match(rawDate, @"(\d{1,2})\s+de\s+([a-z]{3})\s+(\d{4})");
        if (faturaMatch.Success)
        {
            var day = int.Parse(faturaMatch.Groups[1].Value);
            var monthStr = faturaMatch.Groups[2].Value;
            var year = int.Parse(faturaMatch.Groups[3].Value);

            if (MonthMap.TryGetValue(monthStr, out var month))
            {
                try { date = new DateTime(year, month, day); return true; }
                catch { return false; }
            }
        }

        return false;
    }
}