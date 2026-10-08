using CoupleSync.Api.Contracts.Chat;
using CoupleSync.Application.Ai;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class ChatRequestValidator : AbstractValidator<ChatRequest>
{
    private static readonly string[] ValidRoles = ["user", "model"];

    public ChatRequestValidator()
    {
        RuleFor(x => x.Message)
            .NotEmpty()
            .MaximumLength(2000);

        RuleFor(x => x.History)
            .Must(h => h is null || h.Count <= 20)
            .WithMessage("O histórico deve ter no máximo 20 mensagens.");

        RuleForEach(x => x.History)
            .ChildRules(item =>
            {
                item.RuleFor(h => h.Role)
                    .Must(r => ValidRoles.Contains(r, StringComparer.OrdinalIgnoreCase))
                    .WithMessage("O autor de cada mensagem do histórico deve ser 'user' ou 'model'.");

                // Longer than a question: an earlier answer comes back whole and is cut on the server (PromptText).
                item.RuleFor(h => h.Content)
                    .NotEmpty()
                    .MaximumLength(PromptText.HistoryItemMaxLength);
            })
            .When(x => x.History is not null);
    }
}
