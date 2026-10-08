using CoupleSync.Application.Ai;
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
