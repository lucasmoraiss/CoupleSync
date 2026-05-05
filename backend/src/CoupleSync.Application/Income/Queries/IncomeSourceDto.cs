namespace CoupleSync.Application.Income.Queries;

public sealed record IncomeSourceDto(
    Guid Id,
    Guid UserId,
    string Name,
    decimal Amount,
    string Currency,
    bool IsShared,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

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
    decimal CoupleTotal);
