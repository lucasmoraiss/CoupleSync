using CoupleSync.Api.Contracts.Goals;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.ValueObjects;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class UpdateGoalRequestValidator : AbstractValidator<UpdateGoalRequest>
{
    private readonly IDateTimeProvider _dateTimeProvider;

    public UpdateGoalRequestValidator(IDateTimeProvider dateTimeProvider)
    {
        _dateTimeProvider = dateTimeProvider;

        RuleFor(x => x)
            .Must(x => x.Title is not null || x.Description is not null || x.TargetAmount is not null
                || x.CurrentAmount is not null || x.ManualAmount is not null || x.Deadline is not null)
            .WithName("Request")
            .WithMessage("Informe pelo menos um campo para atualizar.");

        RuleFor(x => x.Title)
            .NotEmpty()
            .MaximumLength(128)
            .When(x => x.Title is not null);

        RuleFor(x => x.Description)
            .MaximumLength(512)
            .When(x => x.Description is not null);

        RuleFor(x => x.TargetAmount).PositiveMoney();

        RuleFor(x => x.CurrentAmount).NonNegativeMoney();

        RuleFor(x => x.ManualAmount).NonNegativeMoney();

        // Same rule as CreateGoalRequestValidator: today or later, and only when a deadline is sent.
        RuleFor(x => x.Deadline)
            .Must(d => d!.Value.Date >= BrazilTime.ToLocal(_dateTimeProvider.UtcNow).Date)
            .When(x => x.Deadline.HasValue)
            .WithMessage("O prazo deve ser hoje ou uma data futura.");
    }
}
