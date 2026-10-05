using CoupleSync.Api.Contracts.Ocr;
using FluentValidation;

namespace CoupleSync.Api.Validators;

/// <summary>
/// Shape validation for POST /api/v1/ocr/{uploadId}/confirm. Rules that depend on the
/// import job (e.g. whether an index exists) are enforced by ImportJobService.
/// </summary>
public sealed class ConfirmRequestValidator : AbstractValidator<ConfirmRequest>
{
    public ConfirmRequestValidator()
    {
        RuleFor(x => x.CategoryOverrides)
            .Must(overrides => overrides!.All(o => o is not null)
                && overrides!.Select(o => o.Index).Distinct().Count() == overrides!.Count)
            .When(x => x.CategoryOverrides is not null)
            .WithMessage("CategoryOverrides must not repeat an index.");

        RuleForEach(x => x.CategoryOverrides)
            .ChildRules(o =>
            {
                o.RuleFor(x => x.Category)
                    .Must(c => !string.IsNullOrWhiteSpace(c))
                    .WithMessage("Category is required.")
                    .MaximumLength(64);
            })
            .When(x => x.CategoryOverrides is not null && x.CategoryOverrides.All(o => o is not null));
    }
}
