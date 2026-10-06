using CoupleSync.Api.Contracts.Budget;
using CoupleSync.Domain.ValueObjects;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class UpdateIncomeRequestValidator : AbstractValidator<UpdateIncomeRequest>
{
    public UpdateIncomeRequestValidator()
    {
        RuleFor(x => x.GrossIncome)
            .GreaterThan(0)
            .WithMessage("A renda bruta deve ser maior que zero.")
            .LessThanOrEqualTo(MoneyRules.MaxAmount)
            .WithMessage("A renda bruta excede o máximo permitido.");

        When(x => x.Currency is not null, () =>
        {
            RuleFor(x => x.Currency!)
                .NotEmpty()
                .Length(3)
                .WithMessage("A moeda deve ser um código de 3 letras (ex.: BRL).");
        });
    }
}
