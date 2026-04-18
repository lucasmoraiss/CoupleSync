namespace CoupleSync.Application.Dashboard;

public sealed record GetDashboardQuery(
    Guid CoupleId,
    DateTime? StartDate,
    DateTime? EndDate);
