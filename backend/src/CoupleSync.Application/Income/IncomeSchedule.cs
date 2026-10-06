using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Application.Income;

/// <summary>
/// Which income sources count in a month. A source registered in a month always counts there; a recurring
/// one also counts in every later month, until it is deleted or turned non-recurring. When the same person
/// registers the same name again in a later month, that newer registration replaces the older one from its
/// month on (a raise, for instance) without touching earlier months.
/// </summary>
public static class IncomeSchedule
{
    /// <param name="candidates">Sources of the group already restricted to Month &lt;= month.</param>
    /// <param name="month">"yyyy-MM" Brasília month.</param>
    public static IReadOnlyList<IncomeSource> EffectiveIn(IEnumerable<IncomeSource> candidates, string month)
        => candidates
            .Where(s => string.CompareOrdinal(s.Month, month) == 0
                || (s.IsRecurring && string.CompareOrdinal(s.Month, month) < 0))
            .GroupBy(s => (s.UserId, Name: s.Name.Trim().ToUpperInvariant()))
            .Select(g => g.OrderByDescending(s => s.Month, StringComparer.Ordinal)
                .ThenByDescending(s => s.CreatedAtUtc)
                .First())
            .OrderBy(s => s.CreatedAtUtc)
            .ToList();

    /// <summary>Sum in reais: sources in another currency never enter a total.</summary>
    public static decimal TotalInReais(IEnumerable<IncomeSource> sources)
        => sources.Where(s => CurrencyRules.IsBrl(s.Currency)).Sum(s => s.Amount);
}
