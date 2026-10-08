using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Application.OpenFinance;

/// <summary>Which way the money went, whatever the sign the source uses.</summary>
public enum MoneyDirection
{
    /// <summary>Money that left (a purchase, a transfer sent, a payment).</summary>
    Out,

    /// <summary>Money that came in.</summary>
    In
}

/// <summary>
/// One movement of money, from any source of the app, in plain values. <paramref name="Amount"/> is always positive
/// (the direction says the way); <paramref name="LocalDay"/> is the calendar day in Brazil; <paramref name="AccountId"/>
/// tells one account from another when the source knows it.
/// </summary>
public sealed record MoneyMovement(decimal Amount, string? Currency, DateOnly LocalDay, MoneyDirection Direction, Guid? AccountId = null);

/// <summary>A transaction the app already has, as far as the rules look at it.</summary>
public sealed record ExistingTransactionFacts(Guid Id, decimal Amount, string? Currency, DateOnly LocalDay, TransactionSource Source, string? Bank);

/// <summary>
/// The rules that say when two records are the same money (issue #26): a transfer between accounts of the group, the
/// payment of a credit card bill, a credit that came from someone of the group, and a bank transaction that looks
/// like a transaction the app already has. Pure functions over plain values: nothing here knows Pluggy, the database
/// or the clock, so any source of the app (bank, notification, statement import, manual) can ask the same questions.
/// <para>Only reais take part: a value in another currency, or zero, never matches anything.</para>
/// </summary>
public static class BankReconciliationRules
{
    /// <summary>How many days apart a bank transaction and a transaction of the app may be and still be the same purchase.</summary>
    public const int DuplicateDayTolerance = 1;

    /// <summary>The account type Pluggy gives a checking or savings account (a credit card is <c>CREDIT</c>).</summary>
    public const string BankAccountType = "BANK";

    /// <summary>A name with fewer words than this says too little to tell one person from another.</summary>
    private const int MinWordsOfAName = 2;

    /// <summary>
    /// The banks a transaction of the app can name (the ones the notification capture knows), each with the ways the
    /// name of a connection may spell it, in words already folded.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[][]> KnownBanks = new Dictionary<string, string[][]>(StringComparer.Ordinal)
    {
        ["NUBANK"] = [["NUBANK"], ["NU", "PAGAMENTOS"]],
        ["ITAU"] = [["ITAU"]],
        ["INTER"] = [["INTER"]],
        ["C6"] = [["C6"]],
        ["BRADESCO"] = [["BRADESCO"]],
        ["XP"] = [["XP"]],
        ["BTG"] = [["BTG"]],
        ["SANTANDER"] = [["SANTANDER"]],
        ["CAIXA"] = [["CAIXA"]],
        ["BB"] = [["BB"], ["BANCO", "DO", "BRASIL"]],
    };

    /// <summary>True when the value can be compared with another: in reais and greater than zero.</summary>
    public static bool HasComparableValue(MoneyMovement movement)
        => movement.Amount > 0 && CurrencyRules.IsBrl(movement.Currency);

    /// <summary>
    /// Two legs of one transfer between accounts of the group: same value, opposite ways, same day in Brazil, two
    /// different known accounts.
    /// </summary>
    public static bool IsOwnTransferPair(MoneyMovement one, MoneyMovement other)
        => HasComparableValue(one)
           && HasComparableValue(other)
           && one.Amount == other.Amount
           && one.Direction != other.Direction
           && one.LocalDay == other.LocalDay
           && one.AccountId is { } first
           && other.AccountId is { } second
           && first != second;

    /// <summary>The first of <paramref name="candidates"/>, in the order given, that is the other leg of <paramref name="line"/>.</summary>
    public static T? FindTransferCounterpart<T>(MoneyMovement line, IEnumerable<T> candidates, Func<T, MoneyMovement> facts)
        where T : class
        => candidates.FirstOrDefault(candidate => IsOwnTransferPair(line, facts(candidate)));

    /// <summary>"PAGAMENTO" and "FATURA" in the text, whatever the case and the accents.</summary>
    public static bool IsBillPaymentDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        var folded = Fold(description);
        return folded.Contains("PAGAMENTO", StringComparison.Ordinal) && folded.Contains("FATURA", StringComparison.Ordinal);
    }

    /// <summary>
    /// Money leaving a checking/savings account to pay a credit card bill. Today the description decides. The other
    /// half of the rule (the value equals the total of a closed bill of a card of the group, due within 10 days)
    /// enters here with the bills of the cards (issue #27).
    /// </summary>
    public static bool IsBillPayment(MoneyMovement movement, string? accountType, string? rawDescription)
        => movement.Direction == MoneyDirection.Out
           && HasComparableValue(movement)
           && string.Equals(accountType?.Trim(), BankAccountType, StringComparison.OrdinalIgnoreCase)
           && IsBillPaymentDescription(rawDescription);

    /// <summary>
    /// True when the two names are the same person: every word of <paramref name="holderName"/> (at least two) is a
    /// word of <paramref name="payerName"/>, whatever the case and the accents.
    /// </summary>
    public static bool IsSamePerson(string? payerName, string? holderName)
    {
        var holder = Words(holderName);
        if (holder.Count < MinWordsOfAName) return false;
        var payer = Words(payerName).ToHashSet(StringComparer.Ordinal);
        return holder.All(payer.Contains);
    }

    /// <summary>Money that came in from someone who holds another account of the group.</summary>
    public static bool IsCreditFromGroupHolder(MoneyMovement movement, string? payerName, IEnumerable<string?> holdersOfOtherAccounts)
        => movement.Direction == MoneyDirection.In
           && HasComparableValue(movement)
           && holdersOfOtherAccounts.Any(holder => IsSamePerson(payerName, holder));

    /// <summary>True when the bank of a transaction of the app is one the app knows by name.</summary>
    public static bool IsKnownBank(string? transactionBank) => KnownBankKey(transactionBank) is not null;

    /// <summary>
    /// False only when the bank of the transaction is known and is not the bank of the connection. A transaction
    /// typed by hand or imported from a statement names no bank the app knows: the bank is then not looked at.
    /// </summary>
    public static bool IsSameBankOrUnknown(string? transactionBank, string? connectorName)
    {
        if (KnownBankKey(transactionBank) is not { } key) return true;
        var connector = Words(connectorName);
        return KnownBanks[key].Any(spelling => ContainsInOrder(connector, spelling));
    }

    /// <summary>
    /// A bank transaction (money that left) that looks like a transaction the app already has: the same value, on the
    /// same day in Brazil or one day apart, not itself come from the bank and, when its bank is known, of the same bank.
    /// </summary>
    public static bool IsSuspectedDuplicate(MoneyMovement bankLine, string? connectorName, ExistingTransactionFacts existing)
        => bankLine.Direction == MoneyDirection.Out
           && HasComparableValue(bankLine)
           && CurrencyRules.IsBrl(existing.Currency)
           && existing.Amount == bankLine.Amount
           && existing.Source != TransactionSource.OpenFinance
           && Math.Abs(existing.LocalDay.DayNumber - bankLine.LocalDay.DayNumber) <= DuplicateDayTolerance
           && IsSameBankOrUnknown(existing.Bank, connectorName);

    /// <summary>
    /// The transaction of <paramref name="candidates"/> the line most looks like: the nearest day first, then the
    /// smallest id (so that the same data always gives the same answer); null when none looks like it.
    /// </summary>
    public static ExistingTransactionFacts? PickSuspectedDuplicate(
        MoneyMovement bankLine, string? connectorName, IEnumerable<ExistingTransactionFacts> candidates)
        => candidates
            .Where(candidate => IsSuspectedDuplicate(bankLine, connectorName, candidate))
            .OrderBy(candidate => Math.Abs(candidate.LocalDay.DayNumber - bankLine.LocalDay.DayNumber))
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault();

    /// <summary>The calendar day in Brazil of an instant stored in UTC (how every transaction of the app is dated).</summary>
    public static DateOnly LocalDayOf(DateTime utc) => DateOnly.FromDateTime(BrazilTime.ToLocal(utc));

    private static string? KnownBankKey(string? transactionBank)
    {
        var words = Words(transactionBank);
        return words.Count == 1 && KnownBanks.ContainsKey(words[0]) ? words[0] : null;
    }

    private static bool ContainsInOrder(IReadOnlyList<string> words, string[] spelling)
    {
        for (var start = 0; start + spelling.Length <= words.Count; start++)
        {
            var same = true;
            for (var i = 0; i < spelling.Length && same; i++)
                same = string.Equals(words[start + i], spelling[i], StringComparison.Ordinal);
            if (same) return true;
        }

        return false;
    }

    /// <summary>Without accents and in upper case, by the explicit table (the same answer with or without ICU).</summary>
    private static string Fold(string text) => AccentFolding.RemoveAccents(text).ToUpperInvariant();

    /// <summary>The words of a text, folded: runs of ASCII letters and digits.</summary>
    private static IReadOnlyList<string> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in Fold(text))
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                current.Append(ch);
                continue;
            }

            if (current.Length == 0) continue;
            words.Add(current.ToString());
            current.Clear();
        }

        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }
}
