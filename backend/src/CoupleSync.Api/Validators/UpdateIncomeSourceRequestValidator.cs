using CoupleSync.Api.Contracts.Income;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class UpdateIncomeSourceRequestValidator : AbstractValidator<UpdateIncomeSourceRequest>
{
    public UpdateIncomeSourceRequestValidator()
    {
        RuleFor(x => x.Name)
            .MaximumLength(64)
            .When(x => x.Name is not null);

        RuleFor(x => x.Amount)
            .GreaterThanOrEqualTo(0)
            .When(x => x.Amount is not null);
    }
}
