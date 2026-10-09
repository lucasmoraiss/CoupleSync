using CoupleSync.Application.OpenFinance;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

/// <summary>
/// Open Finance synchronisation runs and the mirror of bank transactions. The reads used by the routes are restricted
/// to the given group; the ones used by the background job say so in their names.
/// </summary>
public interface IBankSyncRepository
{
    // ---------------------------------------------------------------- runs

    Task AddRunAsync(SyncRun run, CancellationToken ct);

    Task<SyncRun?> FindRunAsync(Guid id, Guid coupleId, CancellationToken ct);

    /// <summary>For the job: any group.</summary>
    Task<SyncRun?> FindRunForJobAsync(Guid id, CancellationToken ct);

    /// <summary>The most recently created run of the connection, whatever its status.</summary>
    Task<SyncRun?> GetLatestRunAsync(Guid connectionId, Guid coupleId, CancellationToken ct);

    /// <summary>For the job: the ids of the runs waiting, oldest first, any group.</summary>
    Task<IReadOnlyList<Guid>> GetPendingRunIdsForJobAsync(int max, CancellationToken ct);

    /// <summary>
    /// For the job: the runs in <c>Running</c>, any group. With <paramref name="startedBeforeUtc"/>, only the ones
    /// that started (or, without a start, were created) before that instant.
    /// </summary>
    Task<IReadOnlyList<SyncRun>> GetRunningRunsForJobAsync(DateTime? startedBeforeUtc, CancellationToken ct);

    /// <summary>For the scheduler: every connection that can be synchronised (active or in error), any group.</summary>
    Task<IReadOnlyList<BankConnection>> GetConnectionsToScheduleAsync(CancellationToken ct);

    /// <summary>True when the connection has a run waiting or running, or one created at or after <paramref name="sinceUtc"/>.</summary>
    Task<bool> HasOpenOrRecentRunAsync(Guid connectionId, DateTime sinceUtc, CancellationToken ct);

    // ---------------------------------------------------------------- what a run reads and writes

    Task<IReadOnlyList<BankItem>> GetItemsOfConnectionAsync(Guid connectionId, Guid coupleId, CancellationToken ct);

    /// <summary>Read from the store now: the connection exists, is not disconnected and still holds this encrypted secret.</summary>
    Task<bool> ConnectionStillHasCredentialsAsync(Guid connectionId, string encryptedSecret, CancellationToken ct);

    /// <summary>The day (in Brazil) of the most recent transaction the mirror has of the account; null when it has none.</summary>
    Task<DateOnly?> GetLastTransactionDayAsync(Guid bankAccountId, CancellationToken ct);

    /// <summary>
    /// Tracked: the rows of the account the bank had not settled and nobody reviewed yet, whose instant is in
    /// [<paramref name="fromUtc"/>, <paramref name="toUtcExclusive"/>).
    /// </summary>
    Task<IReadOnlyList<BankTransaction>> GetUnsettledWaitingAsync(Guid bankAccountId, DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct);

    /// <summary>Marks mirror rows to be deleted by the next save.</summary>
    void RemoveTransactions(IEnumerable<BankTransaction> transactions);

    /// <summary>The mirror rows with these Pluggy ids, in any group (the id is unique in the whole database).</summary>
    Task<IReadOnlyList<BankTransaction>> FindByPluggyIdsAsync(IReadOnlyCollection<string> pluggyTransactionIds, CancellationToken ct);

    Task AddTransactionsAsync(IEnumerable<BankTransaction> transactions, CancellationToken ct);

    /// <summary>
    /// For the job, tracked: the lines of the group that can still be the other leg of a transfer, with their day in
    /// Brazil in [<paramref name="fromDay"/>, <paramref name="toDay"/>]: settled at the bank, never brought back to
    /// the review by a person, and either waiting for the review or ignored as a bill payment (whose other leg, the
    /// credit on the card, may arrive later). Oldest first.
    /// </summary>
    Task<IReadOnlyList<BankTransaction>> GetTransferCandidatesForJobAsync(Guid coupleId, DateOnly fromDay, DateOnly toDay, CancellationToken ct);

    /// <summary>
    /// The transactions a line of the bank may still look like: of the group <paramref name="coupleId"/> (named here,
    /// whoever asks: the job or a route), in reais, not come from the bank, whose instant is in
    /// [<paramref name="fromUtc"/>, <paramref name="toUtcExclusive"/>) and that NO line of the mirror is linked to
    /// (neither confirmed from the bank nor reconciled by "it is the same"). A line that only suspects a transaction
    /// does not take it from the others. Each one says whether a line other than <paramref name="exceptLineIds"/>
    /// (the ones being judged now, and the ones leaving the mirror in this very save) already points to it.
    /// Their day is the one <see cref="BankReconciliationRules.LocalDayOf"/> gives.
    /// </summary>
    Task<IReadOnlyList<ExistingTransactionFacts>> GetDuplicateCandidatesForJobAsync(
        Guid coupleId, DateTime fromUtc, DateTime toUtcExclusive, IReadOnlyCollection<Guid> exceptLineIds, CancellationToken ct);

    /// <summary>
    /// Tracked: the lines of the group still waiting for the review that point to one of these transactions as a
    /// suspected duplicate.
    /// </summary>
    Task<IReadOnlyList<BankTransaction>> FindWaitingBySuspectsAsync(IReadOnlyCollection<Guid> transactionIds, Guid coupleId, CancellationToken ct);

    /// <summary>The same question for one transaction, answered with ids only: nothing is tracked.</summary>
    Task<IReadOnlyList<Guid>> FindWaitingIdsBySuspectAsync(Guid transactionId, Guid coupleId, CancellationToken ct);

    /// <summary>
    /// The save of "it is the same": stores everything pending, unless one of <paramref name="transactionIds"/> is
    /// already linked to a line of the mirror other than <paramref name="lineIds"/> (the ones this save links to
    /// them). Then nothing is stored and the answer is false. The transactions are locked before that is looked at,
    /// so that of two answers given at once for two lines that point to one transaction, the second finds the link
    /// of the first: one transaction is never linked to two lines.
    /// </summary>
    Task<bool> SaveLinkingAsync(IReadOnlyCollection<Guid> transactionIds, IReadOnlyCollection<Guid> lineIds, Guid coupleId, CancellationToken ct);

    /// <summary>The automatically ignored lines of the group, of every month: the day in Brazil of each.</summary>
    Task<IReadOnlyList<DateOnly>> GetIgnoredDaysAsync(Guid coupleId, CancellationToken ct);

    /// <summary>For the job: account id → the type Pluggy gives it (<c>BANK</c>, <c>CREDIT</c>), for every account of the group.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetAccountTypesForJobAsync(Guid coupleId, CancellationToken ct);

    /// <summary>For the job: every account of the group with who connected it (its holder) and that person's name.</summary>
    Task<IReadOnlyList<BankAccountHolder>> GetAccountHoldersForJobAsync(Guid coupleId, CancellationToken ct);

    /// <summary>
    /// Saves what a run did so far, unless the credentials of <paramref name="connection"/> are no longer the ones the
    /// run read (disconnected, connected again) or the connection is gone (the person left the group): then nothing
    /// at all is written and it fails with <c>ConcurrencyConflictException</c> or <c>ForeignKeyViolationException</c>.
    /// A line someone reviewed after the run read it keeps that review (state and links): the run writes only what
    /// comes from Pluggy over it, and does not delete it. When that happens, nothing the automatic rules decided in
    /// this save is written as it was: every line the run was changing is back to the review the store has, every
    /// new line is back to waiting, and <paramref name="decideAgain"/> runs over what is stored now before the save
    /// is tried again (a decision about one line depends on the others: the two legs of a transfer).
    /// </summary>
    Task SaveRunProgressAsync(BankConnection connection, Func<CancellationToken, Task>? decideAgain, CancellationToken ct);

    /// <summary>Forgets every change not saved yet (after a failed save, before writing only the verdict of the run).</summary>
    void DiscardChanges();

    // ---------------------------------------------------------------- review

    /// <summary>
    /// The expenses of the group whose day in Brazil is in [<paramref name="from"/>, <paramref name="toExclusive"/>),
    /// waiting or discarded (<see cref="BankTransaction.CountsAsDiscarded"/>).
    /// </summary>
    Task<IReadOnlyList<BankTransaction>> GetReviewExpensesAsync(Guid coupleId, DateOnly from, DateOnly toExclusive, CancellationToken ct);

    /// <summary>The lines of the group ignored automatically (money that left and money that came in) whose day in Brazil is in [<paramref name="from"/>, <paramref name="toExclusive"/>).</summary>
    Task<IReadOnlyList<BankTransaction>> GetIgnoredAsync(Guid coupleId, DateOnly from, DateOnly toExclusive, CancellationToken ct);

    /// <summary>What the review shows of the transactions of the group with these ids (only the ones found).</summary>
    Task<IReadOnlyDictionary<Guid, MatchedTransactionSummary>> GetTransactionSummariesAsync(IReadOnlyCollection<Guid> transactionIds, Guid coupleId, CancellationToken ct);

    /// <summary>Every expense of the group still waiting for the review: its day in Brazil and what says whether it can be confirmed.</summary>
    Task<IReadOnlyList<PendingBankExpense>> GetPendingExpensesAsync(Guid coupleId, CancellationToken ct);

    Task<IReadOnlyList<BankTransaction>> FindByIdsAsync(IReadOnlyCollection<Guid> ids, Guid coupleId, CancellationToken ct);

    Task<BankTransaction?> FindByLinkedTransactionAsync(Guid transactionId, Guid coupleId, CancellationToken ct);

    /// <summary>Tracked: every line of the group that points to the transaction as its own (confirmed from it, or reconciled with it).</summary>
    Task<IReadOnlyList<BankTransaction>> FindAllByLinkedTransactionAsync(Guid transactionId, Guid coupleId, CancellationToken ct);

    /// <summary>Account id → (bank name, account name) for the accounts of the group.</summary>
    Task<IReadOnlyDictionary<Guid, BankAccountNames>> GetAccountNamesAsync(Guid coupleId, CancellationToken ct);

    /// <summary>Fingerprint → id of the transaction of the group that already has it (only the ones found).</summary>
    Task<IReadOnlyDictionary<string, Guid>> FindTransactionIdsByFingerprintsAsync(IReadOnlyCollection<string> fingerprints, Guid coupleId, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record BankAccountNames(string BankName, string AccountName);

public sealed record PendingBankExpense(DateOnly Day, BankTransactionStatus Status, decimal Amount, string Currency);

/// <summary>An account of the group and who connected it. Prints no name.</summary>
public sealed record BankAccountHolder(Guid AccountId, Guid UserId, string? UserName)
{
    public override string ToString() => nameof(BankAccountHolder);
}

/// <summary>What the review shows of the transaction a line looks the same as.</summary>
public sealed record MatchedTransactionSummary(
    Guid Id, TransactionSource Source, string? Description, string? Merchant, decimal Amount, string Currency, DateTime EventTimestampUtc);
