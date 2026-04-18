namespace CoupleSync.Api.Contracts.Budget;

/// <summary>Create or upsert a monthly budget plan. CoupleId is sourced from the JWT — never sent here.</summary>
public sealed record CreateBudgetPlanRequest(
    string Month,
    decimal GrossIncome,
    string Currency);
