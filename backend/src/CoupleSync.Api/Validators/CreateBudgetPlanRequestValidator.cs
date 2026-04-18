using CoupleSync.Api.Contracts.Budget;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class CreateBudgetPlanRequestValidator : AbstractValidator<CreateBudgetPlanRequest>
{
    public CreateBudgetPlanRequestValidator()
    {
        RuleFor(x => x.Month)
            .NotEmpty()
            .Length(7)
            .Matches(@"^\d{4}-(0[1-9]|1[0-2])$")
            .WithMessage("Month must be in YYYY-MM format.");

        RuleFor(x => x.GrossIncome)
            .GreaterThanOrEqualTo(0);

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Length(2, 3);
    }
}
