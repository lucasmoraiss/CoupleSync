using CoupleSync.Api.Contracts.Auth;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress();

        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(120);

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(8);
    }
}
