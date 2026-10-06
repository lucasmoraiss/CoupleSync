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
            .WithMessage("Não repita a mesma transação nas categorias alteradas.");

        RuleForEach(x => x.CategoryOverrides)
            .ChildRules(o =>
            {
                o.RuleFor(x => x.Category).CanonicalCategory();
            })
            .When(x => x.CategoryOverrides is not null && x.CategoryOverrides.All(o => o is not null));

        // candidateEdits: corrections typed by the user on the review screen.
        RuleFor(x => x.CandidateEdits)
            .Cascade(CascadeMode.Stop)
            .Must(edits => edits!.All(e => e is not null))
            .WithMessage("A lista de transações editadas não pode ter itens vazios.")
            .Must(edits => edits!.Select(e => e.Index).Distinct().Count() == edits!.Count)
            .WithMessage("Não repita a mesma transação nas edições.")
            .Must((request, edits) => request.SelectedIndices is null
                || edits!.All(e => request.SelectedIndices.Contains(e.Index)))
            .WithMessage("Só é possível editar transações que estejam selecionadas.")
            .When(x => x.CandidateEdits is not null);

        RuleForEach(x => x.CandidateEdits)
            .ChildRules(edit =>
            {
                edit.RuleFor(e => e.Description)
                    .Must(d => !string.IsNullOrWhiteSpace(d))
                    .WithMessage("A descrição não pode ficar vazia.")
                    .MaximumLength(512)
                    .When(e => e.Description is not null);

                edit.RuleFor(e => e.Amount).PositiveMoney();
            })
            .When(x => x.CandidateEdits is not null && x.CandidateEdits.All(e => e is not null));
    }
}
