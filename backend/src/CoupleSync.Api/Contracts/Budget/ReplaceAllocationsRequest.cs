namespace CoupleSync.Api.Contracts.Budget;

public sealed record AllocationItemRequest(
    string Category,
    decimal AllocatedAmount,
    string Currency);

public sealed record ReplaceAllocationsRequest(
    IReadOnlyList<AllocationItemRequest> Allocations);
