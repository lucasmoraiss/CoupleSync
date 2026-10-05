using CoupleSync.Api.Contracts.Ocr;
using CoupleSync.Domain.ValueObjects;
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

        // candidateEdits: corrections typed by the user on the review screen.
        RuleFor(x => x.CandidateEdits)
            .Cascade(CascadeMode.Stop)
            .Must(edits => edits!.All(e => e is not null))
            .WithMessage("CandidateEdits must not contain null items.")
            .Must(edits => edits!.Select(e => e.Index).Distinct().Count() == edits!.Count)
            .WithMessage("CandidateEdits must not repeat an index.")
            .Must((request, edits) => request.SelectedIndices is null
                || edits!.All(e => request.SelectedIndices.Contains(e.Index)))
            .WithMessage("CandidateEdits may only reference selected indices.")
            .When(x => x.CandidateEdits is not null);

        RuleForEach(x => x.CandidateEdits)
            .ChildRules(edit =>
            {
                edit.RuleFor(e => e.Description)
                    .Must(d => !string.IsNullOrWhiteSpace(d))
                    .WithMessage("Description must not be empty.")
                    .MaximumLength(512)
                    .When(e => e.Description is not null);

                edit.RuleFor(e => e.Amount)
                    .GreaterThan(0)
                    .LessThanOrEqualTo(MoneyRules.MaxAmount)
                    .Must(a => MoneyRules.HasAtMostTwoDecimals(a!.Value))
                    .WithMessage("Amount must have at most two decimal places.")
                    .When(e => e.Amount is not null);
            })
            .When(x => x.CandidateEdits is not null && x.CandidateEdits.All(e => e is not null));
    }
}
