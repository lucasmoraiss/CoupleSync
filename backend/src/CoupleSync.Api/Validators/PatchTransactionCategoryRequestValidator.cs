using CoupleSync.Api.Contracts.Transactions;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class PatchTransactionCategoryRequestValidator : AbstractValidator<PatchTransactionCategoryRequest>
{
    public PatchTransactionCategoryRequestValidator()
    {
        RuleFor(x => x.Category)
            .NotEmpty()
            .MaximumLength(64);
    }
}
