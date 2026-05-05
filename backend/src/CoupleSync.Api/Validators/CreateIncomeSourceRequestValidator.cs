using CoupleSync.Api.Contracts.Income;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class CreateIncomeSourceRequestValidator : AbstractValidator<CreateIncomeSourceRequest>
{
    public CreateIncomeSourceRequestValidator()
    {
        RuleFor(x => x.Month)
            .NotEmpty()
            .Length(7)
            .Matches(@"^\d{4}-(0[1-9]|1[0-2])$")
            .WithMessage("Month must be in YYYY-MM format.");

        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(64);

        RuleFor(x => x.Amount)
            .GreaterThanOrEqualTo(0);

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Length(2, 3);
    }
}
