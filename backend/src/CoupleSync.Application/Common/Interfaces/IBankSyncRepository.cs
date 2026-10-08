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
    /// Saves what a run did so far, unless the credentials of <paramref name="connection"/> are no longer the ones the
    /// run read (disconnected, connected again) or the connection is gone (the person left the group): then nothing
    /// at all is written and it fails with <c>ConcurrencyConflictException</c> or <c>ForeignKeyViolationException</c>.
    /// </summary>
    Task SaveRunProgressAsync(BankConnection connection, CancellationToken ct);

    /// <summary>Forgets every change not saved yet (after a failed save, before writing only the verdict of the run).</summary>
    void DiscardChanges();

    // ---------------------------------------------------------------- review

    /// <summary>The expenses of the group whose day in Brazil is in [<paramref name="from"/>, <paramref name="toExclusive"/>), waiting or discarded.</summary>
    Task<IReadOnlyList<BankTransaction>> GetReviewExpensesAsync(Guid coupleId, DateOnly from, DateOnly toExclusive, CancellationToken ct);

    /// <summary>The day in Brazil of every expense of the group still waiting for the review.</summary>
    Task<IReadOnlyList<DateOnly>> GetPendingExpenseDaysAsync(Guid coupleId, CancellationToken ct);

    Task<IReadOnlyList<BankTransaction>> FindByIdsAsync(IReadOnlyCollection<Guid> ids, Guid coupleId, CancellationToken ct);

    Task<BankTransaction?> FindByLinkedTransactionAsync(Guid transactionId, Guid coupleId, CancellationToken ct);

    /// <summary>Account id → (bank name, account name) for the accounts of the group.</summary>
    Task<IReadOnlyDictionary<Guid, BankAccountNames>> GetAccountNamesAsync(Guid coupleId, CancellationToken ct);

    Task<Guid?> FindTransactionIdByFingerprintAsync(string fingerprint, Guid coupleId, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record BankAccountNames(string BankName, string AccountName);
