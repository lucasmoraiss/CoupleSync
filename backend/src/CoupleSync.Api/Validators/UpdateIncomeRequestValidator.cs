using CoupleSync.Api.Contracts.Budget;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class UpdateIncomeRequestValidator : AbstractValidator<UpdateIncomeRequest>
{
    public UpdateIncomeRequestValidator()
    {
        RuleFor(x => x.GrossIncome)
            .GreaterThan(0)
            .WithMessage("GrossIncome must be greater than zero.");

        When(x => x.Currency is not null, () =>
        {
            RuleFor(x => x.Currency!)
                .NotEmpty()
                .Length(3)
                .WithMessage("Currency must be a 3-letter ISO 4217 code.");
        });
    }
}
