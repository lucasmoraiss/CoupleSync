using CoupleSync.Api.Contracts.Transactions;
using CoupleSync.Domain.ValueObjects;
using FluentValidation;

namespace CoupleSync.Api.Validators;

/// <summary>
/// Edge validation for POST /api/v1/transactions. Limits mirror the column definitions
/// (description/merchant varchar(512), category varchar(64), currency varchar(3), amount numeric(18,2)).
/// </summary>
public sealed class CreateManualTransactionRequestValidator : AbstractValidator<CreateManualTransactionRequest>
{
    public CreateManualTransactionRequestValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThanOrEqualTo(MoneyRules.MinPositiveAmount)
            .WithMessage("O valor deve ser maior que zero.")
            .LessThanOrEqualTo(MoneyRules.MaxAmount)
            .WithMessage("O valor excede o máximo permitido.");

        RuleFor(x => x.Currency)
            .Must(c => c!.Trim().Length == 3 && c.Trim().All(char.IsAsciiLetter))
            .When(x => !string.IsNullOrWhiteSpace(x.Currency))
            .WithMessage("A moeda deve ser um código de 3 letras (ex.: BRL).");

        RuleFor(x => x.Description)
            .MaximumLength(512)
            .When(x => x.Description is not null);

        RuleFor(x => x.Merchant)
            .MaximumLength(512)
            .When(x => x.Merchant is not null);

        RuleFor(x => x.Category)
            .Must(c => !string.IsNullOrWhiteSpace(c))
            .WithMessage("A categoria é obrigatória.")
            .MaximumLength(64);
    }
}
