namespace CoupleSync.Application.Income.Commands;

public sealed record CreateIncomeSourceInput(
    string Name,
    decimal Amount,
    string Currency,
    bool IsShared);

public sealed record UpdateIncomeSourceInput(
    string? Name,
    decimal? Amount,
    bool? IsShared);
