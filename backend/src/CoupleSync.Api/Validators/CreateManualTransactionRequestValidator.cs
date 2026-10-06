using CoupleSync.Api.Contracts.Transactions;
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
        RuleFor(x => x.Amount).PositiveMoney();

        RuleFor(x => x.Currency).BrlCurrency();

        RuleFor(x => x.Description)
            .MaximumLength(512)
            .When(x => x.Description is not null);

        RuleFor(x => x.Merchant)
            .MaximumLength(512)
            .When(x => x.Merchant is not null);

        RuleFor(x => x.Category).CanonicalCategory();
    }
}
