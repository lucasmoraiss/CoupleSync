namespace CoupleSync.Application.Budget.Commands;

/// <summary>Input item for ReplaceAllocations — no ID, generated on creation.</summary>
public sealed record AllocationInput(
    string Category,
    decimal AllocatedAmount,
    string Currency);
