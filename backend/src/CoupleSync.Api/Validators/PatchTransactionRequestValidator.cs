using CoupleSync.Api.Contracts.Transactions;
using FluentValidation;

namespace CoupleSync.Api.Validators;

/// <summary>Edge validation for PATCH /api/v1/transactions/{id}: the same rules as the creation, per field sent.</summary>
public sealed class PatchTransactionRequestValidator : AbstractValidator<PatchTransactionRequest>
{
    private static readonly DateTime EarliestDate = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public PatchTransactionRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => x.Amount is not null || x.Description is not null || x.EventTimestampUtc is not null || x.Category is not null || x.Merchant is not null)
            .WithName("Request")
            .WithMessage("Informe pelo menos um campo para atualizar.");

        RuleFor(x => x.Amount).PositiveMoney();

        RuleFor(x => x.Description)
            .MaximumLength(512)
            .When(x => x.Description is not null);

        RuleFor(x => x.Merchant)
            .MaximumLength(512)
            .When(x => x.Merchant is not null);

        RuleFor(x => x.EventTimestampUtc)
            .Must(d => d!.Value >= EarliestDate)
            .When(x => x.EventTimestampUtc.HasValue)
            .WithMessage("A data da transação é inválida.");

        RuleFor(x => x.Category).CanonicalCategory().When(x => x.Category is not null);
    }
}
