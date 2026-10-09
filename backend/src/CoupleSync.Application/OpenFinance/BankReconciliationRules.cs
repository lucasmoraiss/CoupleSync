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
/// A bank line asking which transaction of the app it looks like. <paramref name="CurrentSuspectId"/> is the one it
/// already points to, when it was judged before (a purchase that was pending at the bank and has now been settled).
/// </summary>
public sealed record SuspicionQuestion(MoneyMovement Line, Guid? CurrentSuspectId = null);

/// <summary>
/// A bill of a credit card of the group, as far as the rules look at it. <paramref name="IsClosed"/> says the total
/// no longer changes (see <see cref="CreditCardBill.IsClosedOn"/>).
/// </summary>
public sealed record CardBillFacts(Guid Id, decimal TotalAmount, string? Currency, DateOnly DueDate, bool IsClosed);

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
    /// Money leaving a checking/savings account to pay a credit card bill, as its description says. The other half of
    /// the rule, the value, is <see cref="FindBillPaid"/>.
    /// </summary>
    public static bool IsBillPayment(MoneyMovement movement, string? accountType, string? rawDescription)
        => CanPayABill(movement, accountType) && IsBillPaymentDescription(rawDescription);

    /// <summary>How many days apart the debit and the due date of the bill may be, before or after, for the debit to be its payment.</summary>
    public const int BillPaymentDayTolerance = 10;

    /// <summary>True for money, in reais and with a value, leaving a checking/savings account: only that pays a bill.</summary>
    public static bool CanPayABill(MoneyMovement movement, string? accountType)
        => movement.Direction == MoneyDirection.Out
           && HasComparableValue(movement)
           && string.Equals(accountType?.Trim(), BankAccountType, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a credit card account.</summary>
    public static bool IsCreditCard(string? accountType)
        => string.Equals(accountType?.Trim(), CreditCardAccountType, StringComparison.OrdinalIgnoreCase);

    /// <summary>A bill a debit can be the payment of: closed (its total no longer changes), in reais, with something to pay.</summary>
    public static bool IsPayableBill(CardBillFacts bill)
        => bill.IsClosed && bill.TotalAmount > 0 && CurrencyRules.IsBrl(bill.Currency);

    /// <summary>
    /// Money that left, of exactly the total of a payable bill that is due at most
    /// <see cref="BillPaymentDayTolerance"/> days before or after the day of the debit.
    /// </summary>
    public static bool PaysBill(MoneyMovement movement, CardBillFacts bill)
        => movement.Direction == MoneyDirection.Out
           && HasComparableValue(movement)
           && IsPayableBill(bill)
           && bill.TotalAmount == movement.Amount
           && Math.Abs(bill.DueDate.DayNumber - movement.LocalDay.DayNumber) <= BillPaymentDayTolerance;

    /// <summary>
    /// The bill of <paramref name="bills"/> the movement pays: the one due nearest to its day, then the one due
    /// first, then the smallest id (so that the same data always gives the same answer); null when it pays none.
    /// </summary>
    public static CardBillFacts? FindBillPaid(MoneyMovement movement, IEnumerable<CardBillFacts> bills)
        => bills
            .Where(bill => PaysBill(movement, bill))
            .OrderBy(bill => Math.Abs(bill.DueDate.DayNumber - movement.LocalDay.DayNumber))
            .ThenBy(bill => bill.DueDate)
            .ThenBy(bill => bill.Id)
            .FirstOrDefault();

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

    /// <summary>
    /// Which transaction each of <paramref name="lines"/> looks like, answered for all of them at once (the answers
    /// come in the order of the lines; null: none). One transaction is the answer of one line only. In this order:
    /// <list type="number">
    /// <item>a line that already points to a transaction keeps it, while that transaction is still among
    /// <paramref name="candidates"/> and still looks like it (<see cref="IsSuspectedDuplicate"/>): the suspicion
    /// never moves to another transaction when the line is judged again;</item>
    /// <item>then every line takes a transaction of its own day in Brazil;</item>
    /// <item>and only the lines still without one take a transaction of the day before or after. Two purchases of
    /// the same value on following days are two purchases: each line looks like the one of its day, never like the
    /// one next to it.</item>
    /// </list>
    /// Within a step the lines are served in the order given, and ties go to the smallest id (the same data always
    /// gives the same answer).
    /// </summary>
    public static IReadOnlyList<ExistingTransactionFacts?> AssignSuspectedDuplicates(
        IReadOnlyList<SuspicionQuestion> lines, string? connectorName, IEnumerable<ExistingTransactionFacts> candidates)
    {
        var free = candidates.ToList();
        var answers = new ExistingTransactionFacts?[lines.Count];

        void Take(int index, ExistingTransactionFacts? transaction)
        {
            if (transaction is null) return;
            answers[index] = transaction;
            free.Remove(transaction);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].CurrentSuspectId is not { } current) continue;
            Take(i, free.FirstOrDefault(c => c.Id == current && IsSuspectedDuplicate(lines[i].Line, connectorName, c)));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (answers[i] is not null) continue;
            var day = lines[i].Line.LocalDay;
            Take(i, PickSuspectedDuplicate(lines[i].Line, connectorName, free.Where(c => c.LocalDay == day)));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (answers[i] is null) Take(i, PickSuspectedDuplicate(lines[i].Line, connectorName, free));
        }

        return answers;
    }

    /// <summary>
    /// The calendar day of a transaction of the app. What a person types and what the notification capture sends is
    /// an instant, stored in UTC: its day is the day in Brazil (21:00 in Brasília is midnight UTC, and still that
    /// day). The statement import stores the date written on the statement with no time, as midnight UTC: that is
    /// the day as written, not 21:00 of the day before in Brasília (the same reading
    /// <see cref="BankTransaction.LocalDateOf"/> gives a line of the bank). An imported line someone edited later
    /// carries the instant typed, and is read as one.
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
