using CoupleSync.Api.Contracts.Income;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class UpdateIncomeSourceRequestValidator : AbstractValidator<UpdateIncomeSourceRequest>
{
    public UpdateIncomeSourceRequestValidator()
    {
        RuleFor(x => x.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name))
            .WithMessage("O nome não pode ficar vazio.")
            .MaximumLength(64)
            .When(x => x.Name is not null);

        RuleFor(x => x.Amount).PositiveMoney();
    }
}
