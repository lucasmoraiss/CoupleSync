using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
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

    public async Task SaveRunProgressAsync(BankConnection connection, CancellationToken ct)
    {
        // Writing a column of the connection back, changed or not, puts its concurrency token (the stored secret)
        // in the WHERE of the save: with other credentials stored, or no row at all, nothing is written.
        _dbContext.Entry(connection).Property(c => c.UpdatedAtUtc).IsModified = true;

        if (!IsPostgres() || _dbContext.Database.CurrentTransaction is not null)
        {
            await DbSaveTranslator.SaveAsync(_dbContext, ct);
            return;
        }

        // The connection is locked before anything is written, the same row the exit from the group locks first
        // (CoupleRepository.RemoveOpenFinanceOfMemberAsync): either this save ends and the exit, which waited,
        // deletes what it stored, or the exit ends first and this save finds no connection. Never the two halfway.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        await _dbContext.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM bank_connections WHERE id = {0} FOR UPDATE", [connection.Id], ct);
        await DbSaveTranslator.SaveAsync(_dbContext, ct);
        await transaction.CommitAsync(ct);
    }

    public void DiscardChanges() => _dbContext.ChangeTracker.Clear();

    // ---------------------------------------------------------------- review

    public async Task<IReadOnlyList<BankTransaction>> GetReviewExpensesAsync(Guid coupleId, DateOnly from, DateOnly toExclusive, CancellationToken ct)
        => await _dbContext.BankTransactions
            .AsNoTracking()
            .Where(t => t.CoupleId == coupleId
                        && t.Type == BankTransactionType.Debit
                        && t.LocalDate >= from
                        && t.LocalDate < toExclusive
                        && ReviewStates.Contains(t.ReviewState))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<DateOnly>> GetPendingExpenseDaysAsync(Guid coupleId, CancellationToken ct)
        => await _dbContext.BankTransactions
            .AsNoTracking()
            .Where(t => t.CoupleId == coupleId
                        && t.Type == BankTransactionType.Debit
                        && t.ReviewState == BankTransactionReviewState.Pending)
            .Select(t => t.LocalDate)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BankTransaction>> FindByIdsAsync(IReadOnlyCollection<Guid> ids, Guid coupleId, CancellationToken ct)
        => await _dbContext.BankTransactions
            .Where(t => t.CoupleId == coupleId && ids.Contains(t.Id))
            .ToListAsync(ct);

    public Task<BankTransaction?> FindByLinkedTransactionAsync(Guid transactionId, Guid coupleId, CancellationToken ct)
        => _dbContext.BankTransactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.CoupleId == coupleId && t.LinkedTransactionId == transactionId, ct);

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

    public async Task<Guid?> FindTransactionIdByFingerprintAsync(string fingerprint, Guid coupleId, CancellationToken ct)
    {
        var ids = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.CoupleId == coupleId && t.Fingerprint == fingerprint)
            .Select(t => t.Id)
            .Take(1)
            .ToListAsync(ct);
        return ids.Count == 0 ? null : ids[0];
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => DbSaveTranslator.SaveAsync(_dbContext, ct);

    private bool IsPostgres()
        => _dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
}
