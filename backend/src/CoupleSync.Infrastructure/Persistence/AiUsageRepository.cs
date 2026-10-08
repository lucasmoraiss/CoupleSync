using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// ai_usage is accounting and has no global group filter (it holds calls without a group, and its global ceilings
/// sum every group on purpose). So the group is never implicit here: a read by group takes the group and names it in
/// the query; the reads without a group are the ones that are global by definition.
/// </summary>
public sealed class AiUsageRepository : IAiUsageRepository
{
    // The calls that count in the budgets: the model answered (well or not), or the request was sent and the caller
    // left before the answer (the provider counted it all the same). A 429, an error or a timeout spent nothing.
    private static readonly string[] SpentTokens =
        [nameof(LlmOutcome.Ok), nameof(LlmOutcome.InvalidOutput), nameof(LlmOutcome.Cancelled)];

    // What decides whether a link is paused or exhausted.
    private static readonly string[] LinkEvents =
        [nameof(LlmOutcome.Ok), nameof(LlmOutcome.RateLimitedMinute), nameof(LlmOutcome.QuotaExhaustedDay)];

    private readonly AppDbContext _dbContext;

    public AiUsageRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(AiUsage usage, CancellationToken ct)
    {
        await _dbContext.AiUsages.AddAsync(usage, ct);
        await DbSaveTranslator.SaveAsync(_dbContext, ct);
    }

    public async Task<AiUsageTotals> GetGroupDayAsync(Guid coupleId, DateOnly dayBrt, IReadOnlyCollection<string> features, CancellationToken ct)
    {
        var inFeatures = features.ToArray();
        var rows = _dbContext.AiUsages.AsNoTracking()
            .Where(u => u.CoupleId == coupleId && u.DayBrt == dayBrt && inFeatures.Contains(u.Feature) && SpentTokens.Contains(u.Outcome));

        var calls = await rows.CountAsync(ct);
        var tokens = await rows.SumAsync(u => (long)u.InputTokens + u.OutputTokens, ct);
        return new AiUsageTotals(calls, tokens);
    }

    public Task<int> CountDayAsync(DateOnly dayBrt, IReadOnlyCollection<string> features, CancellationToken ct)
    {
        var inFeatures = features.ToArray();
        return _dbContext.AiUsages.AsNoTracking()
            .CountAsync(u => u.DayBrt == dayBrt && inFeatures.Contains(u.Feature) && SpentTokens.Contains(u.Outcome), ct);
    }

    public async Task<AiUsageTotals> GetModelDayAsync(string provider, string model, DateOnly dayUtc, CancellationToken ct)
    {
        var rows = _dbContext.AiUsages.AsNoTracking()
            .Where(u => u.DayUtc == dayUtc && u.Provider == provider && u.Model == model);

        var calls = await rows.CountAsync(ct);
        var tokens = await rows.SumAsync(u => (long)u.InputTokens + u.OutputTokens, ct);
        return new AiUsageTotals(calls, tokens);
    }

    public async Task<IReadOnlyList<AiLinkEvent>> GetRecentLinkEventsAsync(string provider, string model, DateTime sinceUtc, int take, CancellationToken ct)
    {
        // The day comes first so that the (day_utc, provider, model) index serves the query.
        var sinceDay = DateOnly.FromDateTime(sinceUtc);
        return await _dbContext.AiUsages.AsNoTracking()
            .Where(u => u.DayUtc >= sinceDay && u.Provider == provider && u.Model == model
                        && u.CreatedAtUtc >= sinceUtc && LinkEvents.Contains(u.Outcome))
            .OrderByDescending(u => u.CreatedAtUtc)
            .Take(take)
            .Select(u => new AiLinkEvent(u.CreatedAtUtc, u.DayUtc, u.Outcome, u.RetryAtUtc))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AiUsageSummaryRow>> GetGroupSummaryAsync(Guid coupleId, DateOnly fromDayBrt, DateOnly toDayBrt, CancellationToken ct)
    {
        var rows = await _dbContext.AiUsages.AsNoTracking()
            .Where(u => u.CoupleId == coupleId && u.DayBrt >= fromDayBrt && u.DayBrt <= toDayBrt)
            .GroupBy(u => new { u.DayBrt, u.Feature, u.Outcome })
            .Select(g => new
            {
                g.Key.DayBrt,
                g.Key.Feature,
                g.Key.Outcome,
                Calls = g.Count(),
                InputTokens = g.Sum(u => (long)u.InputTokens),
                OutputTokens = g.Sum(u => (long)u.OutputTokens),
            })
            .ToListAsync(ct);

        return rows.Select(r => new AiUsageSummaryRow(r.DayBrt, r.Feature, r.Outcome, r.Calls, r.InputTokens, r.OutputTokens)).ToList();
    }

    public async Task<IReadOnlyList<AiUsageModelRow>> GetGroupModelDayAsync(Guid coupleId, DateOnly dayUtc, CancellationToken ct)
    {
        var rows = await _dbContext.AiUsages.AsNoTracking()
            .Where(u => u.CoupleId == coupleId && u.DayUtc == dayUtc)
            .GroupBy(u => new { u.Provider, u.Model })
            .Select(g => new { g.Key.Provider, g.Key.Model, Calls = g.Count() })
            .ToListAsync(ct);

        return rows.Select(r => new AiUsageModelRow(r.Provider, r.Model, r.Calls)).ToList();
    }
}

/// <summary>
/// ai_consents and ai_user_preferences. Both are ICoupleScoped, but the group is always named here and the global
/// filter is bypassed: the gateway asks from background work too, where there is no group of a token.
/// </summary>
public sealed class AiActivationRepository : IAiActivationRepository
{
    private readonly AppDbContext _dbContext;

    public AiActivationRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<AiAcceptance>> GetAcceptancesAsync(Guid coupleId, int version, CancellationToken ct)
    {
        var rows = await InForce(coupleId, version)
            .Join(_dbContext.Users.AsNoTracking(), c => c.UserId, u => u.Id, (c, u) => new { c.UserId, u.Name, c.AcceptedAtUtc })
            .ToListAsync(ct);

        return rows
            .OrderBy(r => r.AcceptedAtUtc)
            .ThenBy(r => r.UserId)
            .Select(r => new AiAcceptance(r.UserId, r.Name, DateTime.SpecifyKind(r.AcceptedAtUtc, DateTimeKind.Utc)))
            .ToList();
    }

    public Task<bool> IsEnabledAsync(Guid coupleId, int version, CancellationToken ct)
        => InForce(coupleId, version).AnyAsync(ct);

    /// <summary>Of this version, not revoked, and of someone who is an active member of the group right now.</summary>
    private IQueryable<AiConsent> InForce(Guid coupleId, int version)
        => _dbContext.AiConsents.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CoupleId == coupleId && c.Version == version && c.RevokedAtUtc == null
                        && _dbContext.CoupleMembers.Any(m => m.CoupleId == coupleId && m.UserId == c.UserId && m.User.IsActive));

    public Task<AiConsent?> FindConsentAsync(Guid coupleId, Guid userId, int version, CancellationToken ct)
        => _dbContext.AiConsents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.CoupleId == coupleId && c.UserId == userId && c.Version == version, ct);

    public async Task<IReadOnlyList<AiConsent>> GetActiveConsentsAsync(Guid coupleId, CancellationToken ct)
        => await _dbContext.AiConsents.IgnoreQueryFilters()
            .Where(c => c.CoupleId == coupleId && c.RevokedAtUtc == null)
            .ToListAsync(ct);

    public async Task AddConsentAsync(AiConsent consent, CancellationToken ct)
        => await _dbContext.AiConsents.AddAsync(consent, ct);

    public Task<AiUserPreference?> FindPreferenceAsync(Guid coupleId, Guid userId, CancellationToken ct)
        => _dbContext.AiUserPreferences.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.CoupleId == coupleId && p.UserId == userId, ct);

    public async Task AddPreferenceAsync(AiUserPreference preference, CancellationToken ct)
        => await _dbContext.AiUserPreferences.AddAsync(preference, ct);

    public Task<bool> IsEmailVerifiedAsync(Guid userId, CancellationToken ct)
        => _dbContext.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.EmailVerified, ct);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await DbSaveTranslator.SaveAsync(_dbContext, ct);
        }
        catch (UniqueViolationException)
        {
            // What could not be stored is forgotten, so that the next save of this request does not try it again.
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }
}

/// <summary>The members of a group as A, B, C... by the order they joined. The group is named in the query.</summary>
public sealed class AiPeopleReader : IAiPeopleReader
{
    private readonly AppDbContext _dbContext;

    public AiPeopleReader(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<AiPerson>> GetPeopleAsync(Guid coupleId, CancellationToken ct)
    {
        var names = await _dbContext.CoupleMembers.AsNoTracking()
            .Where(m => m.CoupleId == coupleId)
            .OrderBy(m => m.JoinedAtUtc)
            .ThenBy(m => m.UserId)
            .Select(m => m.User.Name)
            .ToListAsync(ct);

        return FactPackPrivacyFilter.AsPeople(names);
    }
}
