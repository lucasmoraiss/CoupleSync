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

        RuleFor(x => x.TargetAmount)
            .GreaterThan(0);

        RuleFor(x => x.Currency)
            .Length(2, 3)
            .When(x => x.Currency is not null);

        RuleFor(x => x.Deadline)
            .GreaterThan(_ => _dateTimeProvider.UtcNow)
            .WithMessage("Deadline must be a future date.");
    }
}
