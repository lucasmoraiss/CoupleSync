using CoupleSync.Application.AiFacts;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// recurring_streams and recurring_stream_items, and the projection of the transactions the detector reads. The
/// group is always named in the query, on top of the global filter.
/// </summary>
public sealed class RecurringStreamRepository : IRecurringStreamRepository
{
    private readonly AppDbContext _dbContext;

    public RecurringStreamRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<RecurrenceRow>> ReadProjectionAsync(Guid coupleId, DateTime sinceUtc, int cap, CancellationToken ct)
    {
        var rows = await _dbContext.Transactions.AsNoTracking()
            .Where(t => t.CoupleId == coupleId && t.Currency == CurrencyRules.Brl && t.EventTimestampUtc >= sinceUtc)
            .OrderByDescending(t => t.EventTimestampUtc)
            .ThenBy(t => t.Id)
            .Take(cap)
            .Select(t => new { t.Id, t.EventTimestampUtc, t.Amount, t.Merchant, t.Description, t.Category, t.UserId, t.Source })
            .ToListAsync(ct);

        return rows
            .Select(t => new RecurrenceRow(
                t.Id, DateTime.SpecifyKind(t.EventTimestampUtc, DateTimeKind.Utc), t.Amount, t.Merchant, t.Description, t.Category, t.UserId, t.Source))
            .ToList();
    }

    public async Task<TransactionWatermark> GetWatermarkAsync(Guid coupleId, CancellationToken ct)
    {
        var query = _dbContext.Transactions.AsNoTracking().Where(t => t.CoupleId == coupleId);
        var count = await query.CountAsync(ct);
        if (count == 0) return new TransactionWatermark(0, null);
        var latest = await query.MaxAsync(t => t.CreatedAtUtc, ct);
        return new TransactionWatermark(count, DateTime.SpecifyKind(latest, DateTimeKind.Utc));
    }

    public async Task<DateTime?> GetLastDetectedAtAsync(Guid coupleId, CancellationToken ct)
    {
        var latest = await _dbContext.RecurringStreams.AsNoTracking()
            .Where(s => s.CoupleId == coupleId)
            .MaxAsync(s => (DateTime?)s.DetectedAtUtc, ct);
        return latest is null ? null : DateTime.SpecifyKind(latest.Value, DateTimeKind.Utc);
    }

    public Task<List<RecurringStream>> GetForUpdateAsync(Guid coupleId, CancellationToken ct)
        => _dbContext.RecurringStreams
            .Include(s => s.Items)
            .Where(s => s.CoupleId == coupleId)
            .OrderBy(s => s.MerchantKey).ThenBy(s => s.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RecurringStream>> ListAsync(Guid coupleId, CancellationToken ct)
        => await _dbContext.RecurringStreams.AsNoTracking()
            .Where(s => s.CoupleId == coupleId)
            .ToListAsync(ct);

    public async Task<RecurringTotals> GetTotalsAsync(Guid coupleId, CancellationToken ct)
    {
        // The same rule as RecurringStream.IsHidden, written for the database: what is in the active list and is a
        // commitment (a habit is a projection; a probable instalment is not confirmed).
        var counted = _dbContext.RecurringStreams.AsNoTracking()
            .Where(s => s.CoupleId == coupleId
                        && s.Status != RecurringStatuses.Stopped
                        && s.Confidence != RecurringConfidences.Low
                        && (s.UserOverride == null
                            || s.UserOverride == RecurringOverrides.Subscription
                            || s.UserOverride == RecurringOverrides.FixedBill
                            || (s.UserOverride == RecurringOverrides.Cancelled && s.Flags.Contains(RecurringFlags.ChargedAfterCancel)))
                        && (s.Kind != RecurringKinds.Habit
                            || s.UserOverride == RecurringOverrides.Subscription
                            || s.UserOverride == RecurringOverrides.FixedBill));

        // BOTH branches below must stay in sync. SQLite (integration tests) cannot sum decimal in SQL.
        var isSqlite = _dbContext.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        if (isSqlite)
        {
            var rows = await counted.Select(s => new { s.Cadence, s.MedianAmount, s.AnnualCost }).ToListAsync(ct);
            return new RecurringTotals(
                Round(rows.Sum(r => MonthlyShare(r.Cadence, r.MedianAmount, r.AnnualCost))),
                rows.Sum(r => r.AnnualCost));
        }

        var totals = await counted
            .GroupBy(s => s.CoupleId)
            .Select(g => new
            {
                Monthly = g.Sum(s => s.Cadence == RecurringCadences.Monthly ? s.MedianAmount : s.AnnualCost / 12m),
                Annual = g.Sum(s => s.AnnualCost),
            })
            .FirstOrDefaultAsync(ct);
        return totals is null ? new RecurringTotals(0m, 0m) : new RecurringTotals(Round(totals.Monthly), totals.Annual);
    }

    /// <summary>What a stream costs in a month: its amount when it is monthly, a twelfth of its year otherwise.</summary>
    private static decimal MonthlyShare(string cadence, decimal amount, decimal annualCost)
        => cadence == RecurringCadences.Monthly ? amount : annualCost / 12m;

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public Task<RecurringStream?> FindAsync(Guid coupleId, Guid id, CancellationToken ct)
        => _dbContext.RecurringStreams.FirstOrDefaultAsync(s => s.CoupleId == coupleId && s.Id == id, ct);

    public async Task<IReadOnlyList<RecurringCharge>> GetChargesAsync(Guid coupleId, Guid streamId, CancellationToken ct)
    {
        var rows = await _dbContext.RecurringStreamItems.AsNoTracking()
            .Where(i => i.CoupleId == coupleId && i.StreamId == streamId)
            .Join(
                _dbContext.Transactions.AsNoTracking().Where(t => t.CoupleId == coupleId),
                i => i.TransactionId,
                t => t.Id,
                (i, t) => new { t.Id, t.EventTimestampUtc, t.Amount, t.Merchant, t.Description, t.Bank })
            .OrderByDescending(t => t.EventTimestampUtc)
            .ThenBy(t => t.Id)
            .ToListAsync(ct);

        return rows
            .Select(t => new RecurringCharge(
                t.Id,
                DateTime.SpecifyKind(t.EventTimestampUtc, DateTimeKind.Utc),
                t.Amount,
                !string.IsNullOrWhiteSpace(t.Merchant) ? t.Merchant.Trim() : !string.IsNullOrWhiteSpace(t.Description) ? t.Description.Trim() : t.Bank))
            .ToList();
    }

    public async Task<Dictionary<Guid, string>> GetMemberNamesAsync(Guid coupleId, CancellationToken ct)
        => await _dbContext.CoupleMembers.AsNoTracking()
            .Where(m => m.CoupleId == coupleId)
            .Select(m => new { m.UserId, m.User.Name })
            .ToDictionaryAsync(m => m.UserId, m => m.Name, ct);

    public void Add(RecurringStream stream) => _dbContext.RecurringStreams.Add(stream);

    public void Remove(RecurringStream stream) => _dbContext.RecurringStreams.Remove(stream);

    public void Reset() => _dbContext.ChangeTracker.Clear();

    public Task SaveChangesAsync(CancellationToken ct) => DbSaveTranslator.SaveAsync(_dbContext, ct);
}
