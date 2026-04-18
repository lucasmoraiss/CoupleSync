using CoupleSync.Api.Contracts.Goals;
using CoupleSync.Application.Common.Interfaces;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class UpdateGoalRequestValidator : AbstractValidator<UpdateGoalRequest>
{
    private readonly IDateTimeProvider _dateTimeProvider;

    public UpdateGoalRequestValidator(IDateTimeProvider dateTimeProvider)
    {
        _dateTimeProvider = dateTimeProvider;

        RuleFor(x => x)
            .Must(x => x.Title is not null || x.Description is not null || x.TargetAmount is not null || x.Deadline is not null)
            .WithName("Request")
            .WithMessage("At least one field must be provided for update.");

        RuleFor(x => x.Title)
            .NotEmpty()
            .MaximumLength(128)
            .When(x => x.Title is not null);

        RuleFor(x => x.Description)
            .MaximumLength(512)
            .When(x => x.Description is not null);

        RuleFor(x => x.TargetAmount)
            .GreaterThan(0)
            .When(x => x.TargetAmount is not null);

        RuleFor(x => x.Deadline)
            .GreaterThan(_ => _dateTimeProvider.UtcNow)
            .When(x => x.Deadline.HasValue)
            .WithMessage("Deadline must be a future date.");
    }
}
