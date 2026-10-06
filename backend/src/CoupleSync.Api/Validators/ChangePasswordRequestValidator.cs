using CoupleSync.Api.Contracts.Auth;
using FluentValidation;

namespace CoupleSync.Api.Validators;

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword)
            .NotEmpty();

        // The e-mail comparison needs the stored e-mail, so the handler applies the same PasswordPolicy with it.
        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MeetsPasswordPolicy<ChangePasswordRequest>();
    }
}
