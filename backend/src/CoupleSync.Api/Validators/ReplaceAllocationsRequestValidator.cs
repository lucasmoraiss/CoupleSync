using CoupleSync.Api.Contracts.Budget;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class ReplaceAllocationsRequestValidator : AbstractValidator<ReplaceAllocationsRequest>
{
    public ReplaceAllocationsRequestValidator()
    {
        RuleFor(x => x.Allocations)
            .NotNull()
            .WithMessage("A lista de categorias é obrigatória.");

        RuleForEach(x => x.Allocations)
            .ChildRules(allocation =>
            {
                allocation.RuleFor(a => a.Category).CanonicalCategory();

                allocation.RuleFor(a => a.AllocatedAmount).PositiveMoney();

                allocation.RuleFor(a => a.Currency).BrlCurrency();
            })
            .When(x => x.Allocations is not null);
    }
}
