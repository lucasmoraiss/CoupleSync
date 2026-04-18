namespace CoupleSync.Application.Budget.Queries;

public sealed record BudgetAllocationDto(
    Guid Id,
    string Category,
    decimal AllocatedAmount,
    string Currency,
    decimal ActualSpent,
    decimal Remaining);

public sealed record BudgetPlanDto(
    Guid Id,
    Guid CoupleId,
    string Month,
    decimal GrossIncome,
    string Currency,
    IReadOnlyList<BudgetAllocationDto> Allocations,
    decimal BudgetGap,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
