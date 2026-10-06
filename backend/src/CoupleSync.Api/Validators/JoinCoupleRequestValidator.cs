using CoupleSync.Api.Contracts.Couple;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class JoinCoupleRequestValidator : AbstractValidator<JoinCoupleRequest>
{
    public JoinCoupleRequestValidator()
    {
        RuleFor(x => x.JoinCode)
            .NotEmpty()
            .Matches("^([A-Za-z0-9]{6}|[A-Za-z0-9]{8})$");
    }
}