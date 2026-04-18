namespace CoupleSync.Api.Contracts.Budget;

/// <summary>Response for PATCH /api/v1/budget/income.</summary>
public sealed record UpdateIncomeResponse(
    Guid PlanId,
    string Month,
    decimal GrossIncome,
    string Currency);
