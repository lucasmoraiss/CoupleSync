using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OpenFinance;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// As in <see cref="BankConnectionRepository"/>, every query of a route names the group explicitly on top of the
/// global filter. The queries of the background job (no request, no filter) say "ForJob" and read any group.
/// </summary>
public sealed class BankSyncRepository : IBankSyncRepository
{
    private static readonly SyncRunStatus[] OpenStatuses = [SyncRunStatus.Pending, SyncRunStatus.Running];

    private static readonly BankTransactionReviewState[] ReviewStates =
        [BankTransactionReviewState.Pending, BankTransactionReviewState.Discarded];

    /// <summary>How many times the save of a run is tried while lines of it are being reviewed at the same moment.</summary>
    private const int MaxSaveAttempts = 4;

    private const int ReviewLookupChunk = 500;

    private readonly AppDbContext _dbContext;

    public BankSyncRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ---------------------------------------------------------------- runs

    public Task AddRunAsync(SyncRun run, CancellationToken ct)
        => _dbContext.SyncRuns.AddAsync(run, ct).AsTask();

    public Task<SyncRun?> FindRunAsync(Guid id, Guid coupleId, CancellationToken ct)
        => _dbContext.SyncRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.CoupleId == coupleId, ct);

    public Task<SyncRun?> FindRunForJobAsync(Guid id, CancellationToken ct)
        => _dbContext.SyncRuns.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<SyncRun?> GetLatestRunAsync(Guid connectionId, Guid coupleId, CancellationToken ct)
        => _dbContext.SyncRuns
            .AsNoTracking()
            .Where(r => r.ConnectionId == connectionId && r.CoupleId == coupleId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetPendingRunIdsForJobAsync(int max, CancellationToken ct)
        => await _dbContext.SyncRuns
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Status == SyncRunStatus.Pending)
            .OrderBy(r => r.CreatedAtUtc)
            .Select(r => r.Id)
            .Take(max)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SyncRun>> GetRunningRunsForJobAsync(DateTime? startedBeforeUtc, CancellationToken ct)
    {
        var running = _dbContext.SyncRuns.IgnoreQueryFilters().Where(r => r.Status == SyncRunStatus.Running);
        if (startedBeforeUtc is { } before)
            running = running.Where(r => (r.StartedAtUtc ?? r.CreatedAtUtc) < before);
        return await running.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BankConnection>> GetConnectionsToScheduleAsync(CancellationToken ct)
        => await _dbContext.BankConnections
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.Status != BankConnectionStatus.Disconnected)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(ct);

    public Task<bool> HasOpenOrRecentRunAsync(Guid connectionId, DateTime sinceUtc, CancellationToken ct)
        => _dbContext.SyncRuns
            .IgnoreQueryFilters()
            .AnyAsync(r => r.ConnectionId == connectionId && (OpenStatuses.Contains(r.Status) || r.CreatedAtUtc >= sinceUtc), ct);

    // ---------------------------------------------------------------- what a run reads and writes

    public async Task<IReadOnlyList<BankItem>> GetItemsOfConnectionAsync(Guid connectionId, Guid coupleId, CancellationToken ct)
        => await _dbContext.BankItems
            .Where(i => i.ConnectionId == connectionId && i.CoupleId == coupleId)
            .OrderBy(i => i.CreatedAtUtc)
            .ToListAsync(ct);

    public Task<bool> ConnectionStillHasCredentialsAsync(Guid connectionId, string encryptedSecret, CancellationToken ct)
        => _dbContext.BankConnections
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(c => c.Id == connectionId
                           && c.Status != BankConnectionStatus.Disconnected
                           && c.ClientSecretEncrypted == encryptedSecret, ct);

    public async Task<DateOnly?> GetLastTransactionDayAsync(Guid bankAccountId, CancellationToken ct)
    {
        var days = await _dbContext.BankTransactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.BankAccountId == bankAccountId)
            .OrderByDescending(t => t.LocalDate)
            .Select(t => t.LocalDate)
            .Take(1)
            .ToListAsync(ct);
        return days.Count == 0 ? null : days[0];
    }

    public async Task<IReadOnlyList<BankTransaction>> GetUnsettledWaitingAsync(Guid bankAccountId, DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct)
        => await _dbContext.BankTransactions
            .IgnoreQueryFilters()
            .Where(t => t.BankAccountId == bankAccountId
                        && t.Status == BankTransactionStatus.Pending
                        && t.ReviewState == BankTransactionReviewState.Pending
                        && t.Date >= fromUtc
                        && t.Date < toUtcExclusive)
            .ToListAsync(ct);

    public void RemoveTransactions(IEnumerable<BankTransaction> transactions)
        => _dbContext.BankTransactions.RemoveRange(transactions);

    public async Task<IReadOnlyList<BankTransaction>> FindByPluggyIdsAsync(IReadOnlyCollection<string> pluggyTransactionIds, CancellationToken ct)
        => await _dbContext.BankTransactions
            .IgnoreQueryFilters()
            .Where(t => pluggyTransactionIds.Contains(t.PluggyTransactionId))
            .ToListAsync(ct);

    public Task AddTransactionsAsync(IEnumerable<BankTransaction> transactions, CancellationToken ct)
        => _dbContext.BankTransactions.AddRangeAsync(transactions, ct);

    public async Task<IReadOnlyList<BankTransaction>> GetTransferCandidatesForJobAsync(Guid coupleId, DateOnly fromDay, DateOnly toDay, CancellationToken ct)
        => (await _dbContext.BankTransactions
                .IgnoreQueryFilters()
                .Where(t => t.CoupleId == coupleId
                            && t.Status == BankTransactionStatus.Posted
                            && (t.ReviewState == BankTransactionReviewState.Pending
                                || (t.ReviewState == BankTransactionReviewState.Ignored && t.AutoReason == BankTransactionAutoReasons.BillPayment))
                            && t.PersonDecidedAtUtc == null
                            && t.LocalDate >= fromDay
                            && t.LocalDate <= toDay)
                .ToListAsync(ct))
            .OrderBy(t => t.Date)
            .ThenBy(t => t.PluggyTransactionId, StringComparer.Ordinal)
            .ToList();

    public async Task<IReadOnlyList<ExistingTransactionFacts>> GetDuplicateCandidatesForJobAsync(
        Guid coupleId, DateTime fromUtc, DateTime toUtcExclusive, IReadOnlyCollection<Guid> leavingLineIds, CancellationToken ct)
    {
        var leaving = leavingLineIds.ToList();
        // The value is compared in memory (the rules do it): SQLite keeps decimals as text.
        var rows = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.CoupleId == coupleId
                        && t.Currency == CurrencyRules.Brl
                        && t.Source != TransactionSource.OpenFinance
                        && t.EventTimestampUtc >= fromUtc
                        && t.EventTimestampUtc < toUtcExclusive
                        && !_dbContext.BankTransactions.Any(b =>
                            (b.LinkedTransactionId == t.Id || b.MatchedTransactionId == t.Id) && !leaving.Contains(b.Id)))
            .Select(t => new { t.Id, t.Amount, t.Currency, t.EventTimestampUtc, t.Source, t.Bank })
            .ToListAsync(ct);

        return rows
            .Select(t => new ExistingTransactionFacts(
                t.Id, t.Amount, t.Currency, BankReconciliationRules.LocalDayOf(t.EventTimestampUtc, t.Source), t.Source, t.Bank))
            .ToList();
    }

    public async Task<IReadOnlyList<BankAccountHolder>> GetAccountHoldersForJobAsync(Guid coupleId, CancellationToken ct)
        => await (
                from account in _dbContext.BankAccounts.IgnoreQueryFilters().AsNoTracking()
                join item in _dbContext.BankItems on account.ItemId equals item.Id
                join connection in _dbContext.BankConnections on item.ConnectionId equals connection.Id
                join user in _dbContext.Users on connection.UserId equals user.Id
                where account.CoupleId == coupleId && connection.CoupleId == coupleId
                select new BankAccountHolder(account.Id, user.Id, user.Name))
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, string>> GetAccountTypesForJobAsync(Guid coupleId, CancellationToken ct)
        => await _dbContext.BankAccounts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.CoupleId == coupleId)
            .ToDictionaryAsync(a => a.Id, a => a.Type, ct);

    public async Task SaveRunProgressAsync(BankConnection connection, Func<CancellationToken, Task>? decideAgain, CancellationToken ct)
    {
        // Writing a column of the connection back, changed or not, puts its concurrency token (the stored secret)
        // in the WHERE of the save: with other credentials stored, or no row at all, nothing is written.
        _dbContext.Entry(connection).Property(c => c.UpdatedAtUtc).IsModified = true;

        if (!IsPostgres() || _dbContext.Database.CurrentTransaction is not null)
        {
            await SaveKeepingReviewsAsync(decideAgain, ct);
            return;
        }

        // The connection is locked before anything is written, the same row the exit from the group locks first
        // (CoupleRepository.RemoveOpenFinanceOfMemberAsync): either this save ends and the exit, which waited,
        // deletes what it stored, or the exit ends first and this save finds no connection. Never the two halfway.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        await _dbContext.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM bank_connections WHERE id = {0} FOR UPDATE", [connection.Id], ct);
        await SaveKeepingReviewsAsync(decideAgain, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The save of a run. The review state of a line is its concurrency token: when someone reviewed a line after the
    /// run read it (or the line is gone), the save is refused for that line. The run then drops everything its
    /// automatic rules had decided and not stored: the lines it was changing take the review the store has now (the
    /// one the person gave, or the one they had before the rules), a line it was about to remove stays if it was
    /// reviewed, and the new lines wait again. <paramref name="decideAgain"/> then decides over what is stored now,
    /// and the save is tried again. Dropping only the line that was refused would leave the others decided over a
    /// state that no longer exists: one leg of a transfer ignored and the other discarded by the person. Any other
    /// refusal (the connection, the run itself) is the caller's to handle.
    /// </summary>
    private Task SaveKeepingReviewsAsync(Func<CancellationToken, Task>? decideAgain, CancellationToken ct)
    {
        var attempt = 1;
        return DbSaveTranslator.SaveAsync(
            _dbContext,
            async (refusal, token) =>
            {
                if (refusal.Entries.Count == 0 || refusal.Entries.Any(e => e.Entity is not BankTransaction)) return false;

                // People kept reviewing the same lines under every attempt: the run fails as any other failure of
                // the store (not as a connection that changed) and the next one reads them again.
                if (attempt++ == MaxSaveAttempts)
                    throw new DataStoreException("The lines of the run kept being reviewed while it was saving them.", refusal);

                await TakeReviewsFromStoreAsync(token);
                foreach (var added in _dbContext.ChangeTracker.Entries<BankTransaction>().Where(e => e.State == EntityState.Added).ToList())
                    added.Entity.ForgetAutomaticDecision();
                if (decideAgain is not null) await decideAgain(token);
                return true;
            },
            ct);
    }

    /// <summary>
    /// For every mirror row the pending save changes or deletes: the review stored now replaces the one the run read
    /// and whatever the run had decided about it. A row no longer there (the person left the group) is forgotten; a
    /// row to delete that someone reviewed stays.
    /// </summary>
    private async Task TakeReviewsFromStoreAsync(CancellationToken ct)
    {
        var entries = _dbContext.ChangeTracker.Entries<BankTransaction>()
            .Where(e => e.State is EntityState.Modified or EntityState.Deleted)
            .ToList();

        var stored = new Dictionary<Guid, StoredReview>();
        foreach (var ids in entries.Select(e => e.Entity.Id).Chunk(ReviewLookupChunk))
        {
            var rows = await _dbContext.BankTransactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => ids.Contains(t.Id))
                .Select(t => new StoredReview(
                    t.Id, t.ReviewState, t.LinkedTransactionId, t.LinkedIncomeSourceId, t.MatchedTransactionId, t.AutoReason, t.ReviewedAtUtc,
                    t.PersonDecidedAtUtc))
                .ToListAsync(ct);
            foreach (var row in rows) stored[row.Id] = row;
        }

        foreach (var entry in entries)
        {
            if (!stored.TryGetValue(entry.Entity.Id, out var review))
            {
                entry.State = EntityState.Detached;
                continue;
            }

            // Only what was waiting for the review and for the bank is ever removed by a run.
            if (entry.State == EntityState.Deleted && review.ReviewState != BankTransactionReviewState.Pending)
                entry.State = EntityState.Unchanged;

            Take(entry.Property(t => t.ReviewState), review.ReviewState);
            Take(entry.Property(t => t.LinkedTransactionId), review.LinkedTransactionId);
            Take(entry.Property(t => t.LinkedIncomeSourceId), review.LinkedIncomeSourceId);
            Take(entry.Property(t => t.MatchedTransactionId), review.MatchedTransactionId);
            Take(entry.Property(t => t.AutoReason), review.AutoReason);
            Take(entry.Property(t => t.ReviewedAtUtc), review.ReviewedAtUtc);
            Take(entry.Property(t => t.PersonDecidedAtUtc), review.PersonDecidedAtUtc);
        }
    }

    /// <summary>The stored value becomes both what was read and what is kept: the save does not write the column.</summary>
    private static void Take<TValue>(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry<BankTransaction, TValue> property, TValue stored)
    {
        property.OriginalValue = stored;
        if (property.EntityEntry.State != EntityState.Deleted) property.CurrentValue = stored;
    }

    private sealed record StoredReview(
        Guid Id,
        BankTransactionReviewState ReviewState,
        Guid? LinkedTransactionId,
        Guid? LinkedIncomeSourceId,
        Guid? MatchedTransactionId,
        string? AutoReason,
        DateTime? ReviewedAtUtc,
        DateTime? PersonDecidedAtUtc);

    public void DiscardChanges() => _dbContext.ChangeTracker.Clear();

    // ---------------------------------------------------------------- review

    public async Task<IReadOnlyList<BankTransaction>> GetReviewExpensesAsync(Guid coupleId, DateOnly from, DateOnly toExclusive, CancellationToken ct)
        => await _dbContext.BankTransactions
            .AsNoTracking()
            .Where(t => t.CoupleId == coupleId
                        && t.Type == BankTransactionType.Debit
                        && t.LocalDate >= from
                        && t.LocalDate < toExclusive
                        && (ReviewStates.Contains(t.ReviewState)
                            // Said to be the same as a transaction that was deleted at that very moment: a discarded line.
                            || (t.ReviewState == BankTransactionReviewState.Reconciled && t.LinkedTransactionId == null)))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BankTransaction>> GetIgnoredAsync(Guid coupleId, DateOnly from, DateOnly toExclusive, CancellationToken ct)
        => await _dbContext.BankTransactions
            .AsNoTracking()
            .Where(t => t.CoupleId == coupleId
                        && t.ReviewState == BankTransactionReviewState.Ignored
                        && t.LocalDate >= from
                        && t.LocalDate < toExclusive)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, MatchedTransactionSummary>> GetTransactionSummariesAsync(
        IReadOnlyCollection<Guid> transactionIds, Guid coupleId, CancellationToken ct)
    {
        var found = new Dictionary<Guid, MatchedTransactionSummary>();
        foreach (var ids in transactionIds.Distinct().Chunk(ReviewLookupChunk))
        {
            var rows = await _dbContext.Transactions
                .AsNoTracking()
                .Where(t => t.CoupleId == coupleId && ids.Contains(t.Id))
                .Select(t => new MatchedTransactionSummary(t.Id, t.Source, t.Description, t.Merchant, t.Amount, t.Currency, t.EventTimestampUtc))
                .ToListAsync(ct);
            foreach (var row in rows) found[row.Id] = row;
        }

        return found;
    }

    public async Task<IReadOnlyList<PendingBankExpense>> GetPendingExpensesAsync(Guid coupleId, CancellationToken ct)
        => await _dbContext.BankTransactions
            .AsNoTracking()
            .Where(t => t.CoupleId == coupleId
                        && t.Type == BankTransactionType.Debit
                        && t.ReviewState == BankTransactionReviewState.Pending)
            .Select(t => new PendingBankExpense(t.LocalDate, t.Status, t.Amount, t.Currency))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BankTransaction>> FindByIdsAsync(IReadOnlyCollection<Guid> ids, Guid coupleId, CancellationToken ct)
        => await _dbContext.BankTransactions
            .Where(t => t.CoupleId == coupleId && ids.Contains(t.Id))
            .ToListAsync(ct);

    public Task<BankTransaction?> FindByLinkedTransactionAsync(Guid transactionId, Guid coupleId, CancellationToken ct)
        => _dbContext.BankTransactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.CoupleId == coupleId && t.LinkedTransactionId == transactionId, ct);

    public async Task<IReadOnlyList<BankTransaction>> FindAllByLinkedTransactionAsync(Guid transactionId, Guid coupleId, CancellationToken ct)
        => await _dbContext.BankTransactions
            .IgnoreQueryFilters()
            .Where(t => t.CoupleId == coupleId && t.LinkedTransactionId == transactionId)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, BankAccountNames>> GetAccountNamesAsync(Guid coupleId, CancellationToken ct)
    {
        var rows = await (
                from account in _dbContext.BankAccounts.AsNoTracking()
                join item in _dbContext.BankItems.AsNoTracking() on account.ItemId equals item.Id
                where account.CoupleId == coupleId && item.CoupleId == coupleId
                select new { account.Id, item.ConnectorName, account.Name, account.MarketingName })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.Id,
            r => new BankAccountNames(r.ConnectorName, string.IsNullOrWhiteSpace(r.Name) ? r.MarketingName ?? string.Empty : r.Name));
    }

    public async Task<IReadOnlyDictionary<string, Guid>> FindTransactionIdsByFingerprintsAsync(
        IReadOnlyCollection<string> fingerprints, Guid coupleId, CancellationToken ct)
    {
        if (fingerprints.Count == 0) return new Dictionary<string, Guid>(StringComparer.Ordinal);

        var rows = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.CoupleId == coupleId && fingerprints.Contains(t.Fingerprint))
            .Select(t => new { t.Fingerprint, t.Id })
            .ToListAsync(ct);
        var found = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in rows) found.TryAdd(row.Fingerprint, row.Id);
        return found;
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => DbSaveTranslator.SaveAsync(_dbContext, ct);

    private bool IsPostgres()
        => _dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
}
