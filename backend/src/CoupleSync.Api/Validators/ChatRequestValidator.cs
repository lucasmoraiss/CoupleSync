using CoupleSync.Api.Contracts.Chat;
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
            .WithMessage("History must contain at most 20 items.");

        RuleForEach(x => x.History)
            .ChildRules(item =>
            {
                item.RuleFor(h => h.Role)
                    .Must(r => ValidRoles.Contains(r, StringComparer.OrdinalIgnoreCase))
                    .WithMessage("History item role must be 'user' or 'model'.");

                item.RuleFor(h => h.Content)
                    .NotEmpty()
                    .MaximumLength(2000);
            })
            .When(x => x.History is not null);
    }
}
