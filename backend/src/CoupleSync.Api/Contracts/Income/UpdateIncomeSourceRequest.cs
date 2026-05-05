namespace CoupleSync.Api.Contracts.Income;

public sealed record UpdateIncomeSourceRequest(
    string? Name,
    decimal? Amount,
    bool? IsShared);
