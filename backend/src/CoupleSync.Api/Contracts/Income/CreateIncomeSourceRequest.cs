namespace CoupleSync.Api.Contracts.Income;

public sealed record CreateIncomeSourceRequest(
    string Month,
    string Name,
    decimal Amount,
    string Currency,
    bool IsShared);
