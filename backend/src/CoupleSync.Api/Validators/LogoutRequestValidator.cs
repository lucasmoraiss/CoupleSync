using CoupleSync.Api.Contracts.Auth;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class LogoutRequestValidator : AbstractValidator<LogoutRequest>
{
    public LogoutRequestValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty();
    }
}
