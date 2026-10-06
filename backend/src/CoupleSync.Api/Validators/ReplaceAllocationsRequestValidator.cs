using CoupleSync.Api.Contracts.Budget;
using CoupleSync.Domain.ValueObjects;
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
                allocation.RuleFor(a => a.Category)
                    .NotEmpty()
                    .MaximumLength(64);

                allocation.RuleFor(a => a.AllocatedAmount)
                    .GreaterThanOrEqualTo(0)
                    .LessThanOrEqualTo(MoneyRules.MaxAmount);

                allocation.RuleFor(a => a.Currency)
                    .NotEmpty()
                    .Length(2, 3);
            })
            .When(x => x.Allocations is not null);
    }
}
