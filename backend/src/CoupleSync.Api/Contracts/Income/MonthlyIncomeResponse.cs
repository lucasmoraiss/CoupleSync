namespace CoupleSync.Api.Contracts.Income;

public sealed record IncomeSourceResponse(
    Guid Id,
    Guid UserId,
    string Name,
    decimal Amount,
    string Currency,
    bool IsShared,
    bool IsRecurring,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    // New, additive: true when the source was registered from an entry of the bank (Open Finance).
    bool FromBank = false);

public sealed record IncomeGroupResponse(
    Guid? UserId,
    string? UserName,
    IReadOnlyList<IncomeSourceResponse> Sources,
    decimal Total);

public sealed record MonthlyIncomeResponse(
    string Month,
    string Currency,
    IncomeGroupResponse PersonalIncome,
    IncomeGroupResponse? PartnerIncome,
    IncomeGroupResponse SharedIncome,
    decimal CoupleTotal,
    IReadOnlyList<IncomeGroupResponse> PartnersIncome);
