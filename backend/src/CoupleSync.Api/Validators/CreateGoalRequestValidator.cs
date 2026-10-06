using CoupleSync.Api.Contracts.Goals;
using CoupleSync.Application.Common.Interfaces;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class CreateGoalRequestValidator : AbstractValidator<CreateGoalRequest>
{
    private readonly IDateTimeProvider _dateTimeProvider;

    public CreateGoalRequestValidator(IDateTimeProvider dateTimeProvider)
    {
        _dateTimeProvider = dateTimeProvider;

        RuleFor(x => x.Title)
            .NotEmpty()
            .MaximumLength(128);

        RuleFor(x => x.Description)
            .MaximumLength(512)
            .When(x => x.Description is not null);

        RuleFor(x => x.TargetAmount).PositiveMoney();

        RuleFor(x => x.Currency).BrlCurrency();

        RuleFor(x => x.Deadline)
            .Must(d => d.Date >= _dateTimeProvider.UtcNow.Date)
            .WithMessage("O prazo deve ser hoje ou uma data futura.");
    }
}
