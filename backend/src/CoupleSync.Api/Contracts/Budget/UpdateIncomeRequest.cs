namespace CoupleSync.Api.Contracts.Budget;

/// <summary>PATCH /api/v1/budget/income — update gross income for the current month.</summary>
public sealed record UpdateIncomeRequest(
    decimal GrossIncome,
    string? Currency);
