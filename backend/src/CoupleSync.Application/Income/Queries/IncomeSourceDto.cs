namespace CoupleSync.Application.Income.Queries;

public sealed record IncomeSourceDto(
    Guid Id,
    Guid UserId,
    string Name,
    decimal Amount,
    string Currency,
    bool IsShared,
    bool IsRecurring,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    // True when the source was registered from an entry of the bank (Open Finance) in the review.
    bool FromBank = false);

public sealed record IncomeGroupDto(
    Guid? UserId,
    string? UserName,
    IReadOnlyList<IncomeSourceDto> Sources,
    decimal Total);

public sealed record MonthlyIncomeDto(
    string Month,
    string Currency,
    IncomeGroupDto PersonalIncome,
    IncomeGroupDto? PartnerIncome,
    IncomeGroupDto SharedIncome,
    decimal CoupleTotal,
    IReadOnlyList<IncomeGroupDto> PartnersIncome);
