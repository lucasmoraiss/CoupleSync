using CoupleSync.Api.Contracts.Budget;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class UpdateIncomeRequestValidator : AbstractValidator<UpdateIncomeRequest>
{
    public UpdateIncomeRequestValidator()
    {
        RuleFor(x => x.GrossIncome).PositiveMoney();

        RuleFor(x => x.Currency).BrlCurrency();
    }
}
