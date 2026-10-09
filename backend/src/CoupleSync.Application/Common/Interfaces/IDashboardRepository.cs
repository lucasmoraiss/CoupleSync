using CoupleSync.Application.Dashboard;

namespace CoupleSync.Application.Common.Interfaces;

public interface IDashboardRepository
{
    Task<DashboardAggregates> GetAggregatesAsync(
        Guid coupleId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken ct);

    /// <summary>
    /// The bills of the credit cards of the group (Open Finance) due in [<paramref name="fromDue"/>,
    /// <paramref name="toDue"/>], of connections still connected; null when nobody of the group is connected.
    /// </summary>
    Task<IReadOnlyList<UpcomingBill>?> GetUpcomingBillsAsync(Guid coupleId, DateOnly fromDue, DateOnly toDue, CancellationToken ct);
}
