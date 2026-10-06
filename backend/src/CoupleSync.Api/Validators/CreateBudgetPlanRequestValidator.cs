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
            .WithMessage("O mês deve estar no formato AAAA-MM.");

        RuleFor(x => x.GrossIncome).PositiveMoney();

        RuleFor(x => x.Currency).BrlCurrency();
    }
}
