using CoupleSync.Application.Dashboard;

namespace CoupleSync.Application.Common.Interfaces;

public interface IDashboardRepository
{
    Task<DashboardAggregates> GetAggregatesAsync(
        Guid coupleId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken ct);
}
