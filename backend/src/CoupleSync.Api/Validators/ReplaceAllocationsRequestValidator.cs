using CoupleSync.Api.Contracts.Budget;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class ReplaceAllocationsRequestValidator : AbstractValidator<ReplaceAllocationsRequest>
{
    public ReplaceAllocationsRequestValidator()
    {
        RuleFor(x => x.Allocations)
            .NotNull()
            .WithMessage("Allocations list is required.");

        RuleForEach(x => x.Allocations)
            .ChildRules(allocation =>
            {
                allocation.RuleFor(a => a.Category)
                    .NotEmpty()
                    .MaximumLength(64);

                allocation.RuleFor(a => a.AllocatedAmount)
                    .GreaterThanOrEqualTo(0);

                allocation.RuleFor(a => a.Currency)
                    .NotEmpty()
                    .Length(2, 3);
            })
            .When(x => x.Allocations is not null);
    }
}
