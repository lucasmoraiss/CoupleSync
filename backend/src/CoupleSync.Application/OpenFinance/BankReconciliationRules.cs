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

/// <summary>
/// A transaction the app already has, as far as the rules look at it. <paramref name="PointedByAnotherLine"/>: a line
/// of the bank other than the ones being asked about already looks like it (it only breaks a tie).
/// </summary>
public sealed record ExistingTransactionFacts(
    Guid Id, decimal Amount, string? Currency, DateOnly LocalDay, TransactionSource Source, string? Bank, bool PointedByAnotherLine = false);

/// <summary>
/// A bank line (of the bank named <paramref name="ConnectorName"/>) asking which transaction of the app it looks
/// like. <paramref name="CurrentSuspectId"/> is the one it already points to, when it was judged before.
/// </summary>
public sealed record SuspicionQuestion(MoneyMovement Line, Guid? CurrentSuspectId = null, string? ConnectorName = null);

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

    /// <summary>The account type Pluggy gives a credit card.</summary>
    public const string CreditCardAccountType = "CREDIT";

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

    /// <summary>
    /// False for money that left a credit card: that is a purchase, never one leg of a transfer between accounts of
    /// the group (a purchase of R$ 100,00 on the card of one and R$ 100,00 received by the other on the same day are
    /// two different things). Money that ENTERS a card can be a leg: it is the payment of the bill, whose other leg
    /// left a checking account. An account of a type nobody knows is not held back.
    /// </summary>
    public static bool CanBeTransferLeg(MoneyMovement movement, string? accountType)
        => !(movement.Direction == MoneyDirection.Out
             && string.Equals(accountType?.Trim(), CreditCardAccountType, StringComparison.OrdinalIgnoreCase));

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
    /// False only when both sides name a bank the app knows and the two are not the same bank. A transaction typed
    /// by hand or imported from a statement names no bank the app knows; and the name of a connection may say nothing
    /// about the bank (a connection made through "Meu Pluggy" may be named after it, or have no name at all). With
    /// either side unknown there is nothing to compare: the bank is not looked at, and the suspicion stays.
    /// </summary>
    public static bool IsSameBankOrUnknown(string? transactionBank, string? connectorName)
    {
        if (KnownBankKey(transactionBank) is not { } key) return true;
        var connector = Words(connectorName);
        if (Names(connector, key)) return true;
        return !KnownBanks.Keys.Any(other => Names(connector, other));
    }

    /// <summary>
    /// A bank transaction (money that left) that looks like a transaction the app already has: the same value, on the
    /// same day in Brazil or one day apart, not itself come from the bank and not of another bank
    /// (<see cref="IsSameBankOrUnknown"/>).
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
    /// Which transaction each of <paramref name="lines"/> looks like (the answers come in the order of the lines;
    /// null: none). <paramref name="candidates"/> are the transactions no line of the bank is linked to yet. A line
    /// has an answer whenever at least one candidate looks like it (<see cref="IsSuspectedDuplicate"/>), whatever
    /// the other lines are and whatever the order: one transaction may be the answer of several lines at once (two
    /// lines of the same value, and only one of them is the purchase the app has: both ask the person). Which one
    /// is shown, in this order:
    /// <list type="number">
    /// <item>the one the line already points to, while it is still a candidate and still looks like it;</item>
    /// <item>one of the same day in Brazil: purchases of the same value on different days are different purchases;</item>
    /// <item>one of the day before or after, only when none is of the same day.</item>
    /// </list>
    /// Ties go to a transaction no other line points to (so that two equal purchases and two equal lines show one
    /// each), then to the smallest id (the same data always gives the same answer).
    /// </summary>
    public static IReadOnlyList<ExistingTransactionFacts?> AssignSuspectedDuplicates(
        IReadOnlyList<SuspicionQuestion> lines, IEnumerable<ExistingTransactionFacts> candidates)
    {
        var all = candidates.ToList();
        var answers = new ExistingTransactionFacts?[lines.Count];
        var pointed = all.Where(c => c.PointedByAnotherLine).Select(c => c.Id).ToHashSet();

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].CurrentSuspectId is not { } current) continue;
            answers[i] = all.FirstOrDefault(c => c.Id == current && IsSuspectedDuplicate(lines[i].Line, lines[i].ConnectorName, c));
            if (answers[i] is not null) pointed.Add(current);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (answers[i] is not null) continue;
            var line = lines[i];
            answers[i] = all
                .Where(c => IsSuspectedDuplicate(line.Line, line.ConnectorName, c))
                .OrderBy(c => Math.Abs(c.LocalDay.DayNumber - line.Line.LocalDay.DayNumber))
                .ThenBy(c => pointed.Contains(c.Id))
                .ThenBy(c => c.Id)
                .FirstOrDefault();
            if (answers[i] is { } answer) pointed.Add(answer.Id);
        }

        return answers;
    }

    /// <summary>
    /// The calendar day of a transaction of the app. What a person types and what the notification capture sends is
    /// an instant, stored in UTC: its day is the day in Brazil (21:00 in Brasília is midnight UTC, and still that
    /// day). The statement import stores the date written on the statement with no time, as midnight UTC: that is
    /// the day as written, not 21:00 of the day before in Brasília (the same reading
    /// <see cref="BankTransaction.LocalDateOf"/> gives a line of the bank). An imported line someone edited later
    /// keeps the instant typed, which is read as one unless it is exactly midnight UTC. Midnight UTC is also what the
    /// edit form sends back when the person changes only the date of an imported line (it shows that line as 21:00 of
    /// the day before): then the day is read one day ahead of the date the person chose. Nothing stored tells the
    /// two apart (a transaction keeps no mark of an edit), so this reading is the rule; the root is the import,
    /// which stores a date with no time as midnight UTC.
    /// </summary>
    public static DateOnly LocalDayOf(DateTime utc, TransactionSource source)
        => source == TransactionSource.OcrImport && utc.TimeOfDay == TimeSpan.Zero
            ? DateOnly.FromDateTime(utc)
            : DateOnly.FromDateTime(BrazilTime.ToLocal(utc));

    /// <summary>
    /// The instants (UTC, the end left out) between which a transaction of the app can be a duplicate of a bank line
    /// of <paramref name="firstDay"/> to <paramref name="lastDay"/>. It starts at midnight UTC of the earliest day
    /// that counts, where a date stored with no time lies (three hours before that day begins in Brazil), and ends
    /// with the latest day that counts in Brazil. It may bring a few transactions too many, never too few: the
    /// rules look at the day of each one.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtcExclusive) DuplicateSearchWindowUtc(DateOnly firstDay, DateOnly lastDay)
        => (DateTime.SpecifyKind(firstDay.AddDays(-DuplicateDayTolerance).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            BrazilTime.ToUtc(lastDay.AddDays(DuplicateDayTolerance + 1).ToDateTime(TimeOnly.MinValue)));

    private static string? KnownBankKey(string? transactionBank)
    {
        var words = Words(transactionBank);
        return words.Count == 1 && KnownBanks.ContainsKey(words[0]) ? words[0] : null;
    }

    /// <summary>True when the words of the name of a connection spell the bank of <paramref name="key"/>.</summary>
    private static bool Names(IReadOnlyList<string> connectorWords, string key)
        => KnownBanks[key].Any(spelling => ContainsInOrder(connectorWords, spelling));

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
