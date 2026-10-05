using CoupleSync.Api.Contracts.Income;
using CoupleSync.Domain.ValueObjects;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class UpdateIncomeSourceRequestValidator : AbstractValidator<UpdateIncomeSourceRequest>
{
    public UpdateIncomeSourceRequestValidator()
    {
        RuleFor(x => x.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name))
            .WithMessage("Name must not be empty.")
            .MaximumLength(64)
            .When(x => x.Name is not null);

        RuleFor(x => x.Amount)
            .GreaterThanOrEqualTo(0)
            .LessThanOrEqualTo(MoneyRules.MaxAmount)
            .When(x => x.Amount is not null);
    }
}
