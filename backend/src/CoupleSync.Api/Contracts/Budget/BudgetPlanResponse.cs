namespace CoupleSync.Api.Contracts.Budget;

public sealed record BudgetAllocationResponse(
    Guid Id,
    string Category,
    decimal AllocatedAmount,
    string Currency,
    decimal ActualSpent,
    decimal Remaining);

public sealed record BudgetPlanResponse(
    Guid Id,
    string Month,
    decimal GrossIncome,
    string Currency,
    IReadOnlyList<BudgetAllocationResponse> Allocations,
    decimal BudgetGap,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
